# Session 75 — Per-position climate, and the season the game was actually drawing

**Date:** `2026-09-02`
**Branch/commit:** `codex/seasonal-tint-blend-weight`, commits `0190e8b` and `40d8e46`
**Mod version:** `0.3.177` - `0.3.183`; 0.3.177, 0.3.180 and 0.3.183 human-tested and accepted
**Assist protocol / blob / schema:** `1 / 4 / 6`

## Context and investigation

Session 74 closed with the blend band "dramatically worse" against a build whose core correction
the owner had accepted. This session fixed that, then built Phase 2 of
`dev/plans/PLAN_DISTANT_TINT_SAMPLING.md` - one tint per position instead of one for the whole
world - and in the course of instrumenting it found a defect that predates every previous colour
session and explains why the match had looked "nearly perfect" a week earlier and did not now.

Nothing was launched by this session. Every measurement is either the owner's in-game reading,
his screenshots, or the game's own assets and IL read off disk.

## 1. The band was drawing the tint table as its ground texture

Two defects had to line up, and either alone would have been invisible.

`LoadOrUpdateTextureFromBgra` reaches `GL.BindTexture` with no `GL.ActiveTexture` of its own and
never restores what it displaced, so the texture it writes stays bound to whichever unit happened
to be current. The tint ramp's upload sat one line after the block atlas was bound to unit 9, so
it took unit 9. The blend band is the only place the atlas is sampled, which is why every fragment
inside the band drew a 64x96 tint ramp as its block texture - several times too bright, since the
ramp's texels are diluted tints near white while a block mean is 0.2-0.45 - and every fragment
outside it was perfect.

Alone that would have been one frame in eighteen hundred. The second defect made it permanent:
`UploadTintRamp` assigned the field `uploadedTintVersion` while the two indirect programs passed
their own counters by ref, so their guard never closed and the upload ran on every frame of every
indirect pass. The ramp is a texture, not program state; one counter per program was a shape
carried forward from when the table was uniform arrays.

Both were established by reading `ClientPlatformWindows.LoadOrUpdateTextureFromPixels` and
`ShaderProgramBase.BindTexture2D` in IL: the first calls `GL.BindTexture` on both branches with no
`ActiveTexture`, the second sets `Uniform1`, then `ActiveTexture`, then `BindTexture`, leaving that
unit current.

Every upload now goes through `UploadOwnTexture`, which makes the destination unit current first,
so a stray bind lands where the texture belongs anyway. The five units this renderer owns are
named constants rather than ten literals. Seven static checks pin the rule.

## 2. Phase 2, and the wall it hit

The plan's Phase 2 stores one finished colour per (slot, cell). Reading the engine's own
`colormap.vsh` changed the design before any of it was built:

- **The season map's column is the calendar, not climate.** `seasonColorMapUv.x` is
  `rect.x + rect.z * clamp(seasonRel + b/40, 0.01, 0.99)`. Climate never enters. Section 3 of the
  plan says it does, and that was half of what Phase 2 was sized to carry.
- **The entire spatial dependence of a tint is two bytes**: raw temperature and rainfall.
  Everything else is arithmetic (the lapse rate, the season weight) or a map lookup.

So the field stores climate, not colour: two bytes per 64-block cell, no slot axis, one climate
read per cell rather than one colour call per (slot, cell, height). The shader finishes the
colour. Height stops being interpolated and becomes exact - the 96-step ramp exists only as a
fallback now.

**Then the wall.** `ClientWorldMap.GetClimate` reads the map region covering a position and, when
the client does not hold that region, returns the constant `0xB4B4B4` - temperature 180, rainfall
180 - rather than failing. A per-cell sampler built on it would paint most of the horizon one
invented colour and look like a tuning problem. `IBlockAccessor.GetMapRegion(rx, rz)?.ClimateMap`
is the honest test and is public.

`.vhclimate` was written to measure how far real climate actually reaches. The owner ran it three
times: on load, after flying, and back at the start.

| run | fully covered | last partial | LOD draws to |
|---|---|---|---|
| on load | 512 | 1024 | 8192 |
| after flying | 768 | 3072 | 8192 |
| returned | 1024 | 3072 | 8192 |

Real climate reaches about a thousand blocks, growing toward three thousand as the player
explores, and it accumulates and persists for the session. **This limits Phase 2 for both designs,
including the plan's**, and nobody had checked it.

Unknown cells are therefore filled by spreading outward from the nearest cell that has an answer -
the seam field's chamfer sweep, carrying the climate along with the distance. There is no line in
the terrain where knowledge ends, the far field takes the nearest real climate instead of the
player's own, and with nothing known at all it degrades to exactly the old behaviour.

The same probe measured the gradient: raw temperature moves 2.3 counts per 64 blocks and rainfall
10.8, and both scale **exactly linearly** from 32 to 256 blocks. Climate is a smooth ramp at that
scale, so interpolating between cell centres costs curvature only. 64-block cells - one level-0
section edge, so a section origin is cell-aligned at every LOD level.

## 3. Three defects found by the owner's testing, in order

**Blue forest.** `ColorMap.Pixels` keeps red in the low byte and every texture this renderer
uploads wants it at bits 16-23. The copy was verbatim, so red and blue traded places and an autumn
hillside drew deep blue against vanilla's orange. Not a guess about the loader: `LoadColorMaps`
calls `switchRedAndBlueChannels` over the array at load, which is why the tint the mod has always
got from `ApplyColorMapOnRgba` has red high while the array behind it does not. A grey test pixel
is unchanged by the swap, which is why the check now uses an orange one and asserts the grey case
as the reason.

**A flicker back to the old system, once a cadence.** One counter of known cells served two jobs:
describing the field on screen and counting the one being built. `BeginRefresh` reset it, `Active`
went false for the length of the rebuild, and the renderer read that as "no climate anywhere".
Two counters now.

**Rainfall spent at the wrong height.** `Climate.GetRainFall(raw, y)` adds
`(y - Sealevel)/2 + 5*clamp(8 + Sealevel - y, 0, 8)` before anything reads a colour map. The field
sampled rainfall at sea level - exactly where that bonus peaks - and used it unchanged everywhere:
35 counts too wet ten blocks up, 30 at twenty, gone only by eighty. The height ramp never had the
fault, because it asked the engine for a finished colour at each height and the engine applied
this itself. The field stores raw rainfall and the shader adds the terms back. Note the rule keys
on `Climate.Sealevel`, a different static from the world map's sea level the lapse rate uses; both
are passed rather than assumed equal.

## 4. The season was ten days behind, and always had been

After the rainfall fix the owner reported almost no change and pushed back on an explanation of
mine that attributed the residual to cloud shadow and distance. He was right and it was wrong.

Re-measuring the three screenshots over only the brightest quarter of each region - lit against
lit, which removes the cloud shadows on his placed platform - gives one consistent signature:
**green matches within 1%, red is 7% high, blue is 12% low.** A hue error, not brightness.

Everything the mod could still have been wrong about was then checked and eliminated:

- the climate map lookup, computed independently from `planttint.png`: R0.787 G0.538 B0.105
  against the mod's R0.788 G0.540 B0.105;
- the stored colour and the untinted share, against the overlay texture itself: top-tile coverage
  1.0000 and colour (151.9, 153.8, 126.8) against the mod's (152, 154, 127), so a share of exactly
  zero is correct for full grass and not a regression;
- the blend and the weight, against `colormap.fsh` and `chunktopsoil.fsh`;
- the recovered season colour, fitted against all 128 columns of `grasstint.png`: it lands 2.4
  counts from column 52's row mean, so the recovery was faithful to what it was asked for.

What was left is what it was asked for. The CPU colour call reads the season map at
`YearRel * width`; `colormap.vsh` reads it at `(seasonRel + b/40) * width` where
`b = valuenoise(p) + valuenoise(p/2)`, so the shader advances the season by up to 0.05 of a year
and on average 0.025 - about ten in-game days, three columns of the map - and the CPU adds nothing.
Everything the mod knew about the season came from that call.

Deriving the colour cost from the map and the owner's own climate readings, independently of the
screenshots:

| | predicted | measured |
|---|---|---|
| shot 1 | +5.6% red | +5.6% |
| shot 2 | +5.7% | +5.4% |
| shot 3 | +6.3% | +6.4% |

**Why it had looked perfect.** The offset is constant; what changes is how fast the map is moving.
Through midsummer three columns are worth almost nothing. Through the autumn turn they are worth
ten counts of red. The defect is as old as the tint and only just became visible - which also
means no amount of work on the climate field was ever going to fix it, and two rounds of chasing
it there produced nothing.

The season map is now read directly, at the shader's column, using the engine's own
`DefaultShaderUniforms.SeasonRel` - the very value it hands its terrain shaders, so the mod cannot
disagree with it about the time of year. That retires the algebraic recovery as well, along with
one colour call per sample.

Self-review of that change caught a defect before packaging: water carries a climate map and no
season map, and the ramp would have blended it toward a season colour that was never read,
dragging distant water toward black by the weight.

---

## Delivered

- **0.3.177** - `UploadOwnTexture`, named texture units, one ramp upload counter. Human-tested:
  the band is fixed.
- **0.3.178** - `.vhclimate`, and `LodClimateProbe` with its ring geometry checked.
- **0.3.179** - the climate field: `LodClimateField`, `LodClimateMapPages`, the slot table, the
  vertex shader path, `.vhtint field on|off`.
- **0.3.180** - the red/blue swap and the refresh flicker. Human-tested: both fixed.
- **0.3.181** - `LodClimateMath`, rainfall stored raw and reconstructed at height.
- **0.3.182** - `.vhtint` reports the field path's own arithmetic term by term beside the game's
  own answer.
- **0.3.183** - `LodSeasonColor`: the season map read at the shader's column. Human-tested:
  "Much better, Excellent!"
- Checks 12,221 -> **22,584**. Several read the game's own shader source rather than a copy of it.

## Decisions

- **The field stores climate, not colour.** Two bytes per cell against a colour per (slot, cell,
  height); one climate read per cell against roughly 370,000 colour calls per refresh for the
  plan's shape. Cost: about four lines transcribed from `colormap.vsh`, held to the engine's own
  source by a check rather than to a copy of it in this repo.
- **The colour maps are copied, not sampled from the block atlas.** The atlas rect would have to
  be read out of engine state, the maps can land on any atlas page, and each map's padding is
  inside that rect. Owning the copy costs about a megabyte and removes all three questions.
- **Unknown cells spread, they do not cut off.** A boundary in the data would be a boundary in the
  terrain, which is the artifact class this project has spent the most time removing.
- **64-block cells**, because that is one level-0 section edge and because the measured gradient is
  linear well past it. Not chosen for cost: the cost is trivial either way.
- **The height ramp is kept as a fallback** for slots the field cannot finish - no paged climate
  map, or the tropical season weight this mod does not reproduce. It is scheduled for removal now
  that the field path is accepted.
- **`.vhtint field on|off`** kept, so the two paths can be compared in place. Same reason the
  weight switch exists.

## Traps

**An upload that binds on the current texture unit steals a neighbour's sampler.** Trigger: any
`LoadOrUpdateTextureFrom*` call. Failure: section 1. Safer: make the destination unit current
first, and keep every upload behind one helper so the next one cannot forget. Promoted as G143.

**The engine's CPU colour call is not what the engine draws.** Trigger: taking a tint, a weight or
a season from `ApplyColorMapOnRgba` and expecting the terrain shader to agree. Failure: section 4,
and the season weight in Session 74. Safer: read `colormap.vsh` for the quantity in question and
transcribe it, holding the transcription against the engine's own file in a check. Promoted as
G144.

**An average cannot be held to within a few percent of a single random draw.** Trigger: comparing
a deliberately averaged summary against the engine's answer for one block. Failure: the blue
channel of `.vhtint`'s C line spans 0.004..0.090 across the 64 lattice positions, so a 37% gap
against one draw is noise; the plan's own acceptance criterion is written this way and cannot
pass. Safer: compare against the lattice mean, and print the spread beside any single-sample
reading. Promoted as G145.

**A value read where its height term peaks, then spent at every height.** Trigger: sampling a
climate quantity at one convenient height. Failure: rainfall, section 3. Safer: check whether the
engine adds anything height-dependent before the value is used, and store the raw quantity with
the reconstruction in one named place. Promoted as G146.

**Reasoning from the old path's numbers while the new path draws.** Not promoted, but it cost two
rounds this session: `.vhtint` reported the ramp's tint while the field was what reached the
screen, and both explanations built on it were wrong. The diagnostic now prints the drawing path's
own terms.

## Flagged and unverified

Judgement calls awaiting human review:

- Cell size 64 and window 128 cells (+/-4,096 blocks). Terrain past the window clamps to the edge.
- The season column jitter is integrated over nine points with triangular weights. Value noise is
  not triangular; the whole spread is worth about 1% of the final colour, and the mean shift -
  which is the real term - is exact.
- Rows of the season map are averaged flat. The shader's clamped noise favours the two end rows;
  measured against this map that is worth under 4 counts on any column.

Claims that lack the evidence level required to treat them as established:

- No shader in this session was compile-verified offline; there is still no GLSL validator in the
  toolchain. Every shader claim is source-traced plus human-tested.
- The climate field's coverage figures are one world on one machine on one afternoon.
- Whether anything remains of the colour mismatch after 0.3.183 is unmeasured. The owner's report
  is "much better"; no post-fix screenshot has been differenced.
