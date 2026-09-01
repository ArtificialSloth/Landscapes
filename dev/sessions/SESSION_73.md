# Session 73 — Distant terrain colour: three causes measured and fixed

**Date:** `2026-08-31`
**Branch/commit:** `codex/exact-vanilla-draw-range`, from `7a44e6b`
**Mod version:** `0.3.167` - `0.3.171`; 0.3.169 and 0.3.170 human-tested and accepted
**Assist protocol / blob / schema:** `1 / 4 / 6`

> Session records are Tier 3 history. Write narrative as needed, but preserve the four required tail sections so future harvesting remains mechanical.

## Context and investigation

The owner reported that cached terrain still did not match vanilla's colour, after earlier
sessions had already fixed four separate causes (Sessions 30-31, G46-G49) and moved to drawing
the game's own textures at the seam (Session 65). Two screenshots were supplied, then a third,
much better one: a flat two-block test patch built deliberately, medium-fertility grassy soil on
the left and granite gravel on the right, cached above and vanilla below, with `.vhband speckle`
set to zero so nothing was disguising the average.

Everything below was measured offline. No game was launched by this session; every in-game
observation is the owner's.

### 1. Method: measuring a screenshot instead of arguing about it

A screenshot is not the shader's output. Vintage Story's `final.fsh` applies a gamma of 2.2 to
HSL *lightness*, 0.2 sepia, a bloom add and an SSAO multiply, then blends the graded and ungraded
images by the pixel's own luma. So the pixel values were pushed back through an inverse of that
chain, from the owner's actual `clientsettings.json`, to recover what the terrain shaders
produced. The ratio between cached and vanilla is invariant to the bloom gain, which is the one
term that could not be pinned, so the ratios below are trustworthy even though the absolute
values are not.

Measured on the test patch, in shader space:

| | cached | vanilla | ratio |
|---|---|---|---|
| granite gravel | 218.1, 205.0, 161.4 | 157.1, 146.7, 129.3 | 1.389, 1.397, 1.248 |
| medium-fert soil, grass | 143.2, 115.6, 33.5 | 124.9, 106.4, 17.9 | 1.146, 1.087, 1.875 |

Two different signatures on one build: stone is a pure brightness error with the hue intact;
grass is nearly the right brightness with the blue almost doubled. That is what said there were
at least two independent causes, and it is why every previous single global correction failed -
they pull in opposite directions and their sum depends on whether the block has a grass overlay.

Normalising each colour to its own red identified both sides beyond doubt: cached gravel came out
`1, 0.940, 0.740` against the mod's stored palette colour of `1, 0.930, 0.734`, and vanilla gravel
came out `1, 0.934, 0.823` against `granite.png`'s own average of `1, 0.937, 0.830`. Cached ground
was drawing exactly the colour the mod had stored, and vanilla was drawing exactly the average of
its own texture. Nothing else was bending the hue, so the whole gap was two magnitudes.

### 2. Cause one: sky light is not full light

`applyLight` in `fogandlight.vsh` multiplies by `lightColor.a`, the sun light level baked into
every terrain vertex. Session 47's derivation (recorded in STATUS and shipped in 0.3.51) reduced
that for the LOD case as "full sky light ... every scale factor cancels except the contrast
constant", leaving `1.05 * ambientColor`. The cancellation is wrong: `bMax` renormalisation
cancels the ambient colour's own *mean*, and the sun level survives it and multiplies the pixel.

Sky-exposed ground is not level 31 either. `assets/survival/config/general.json` sets
`sunBrightness: 22`, and `sunLightLevels[22]` is 0.806. Confirmed by invoking the engine's own
`ColorUtil.LightUtil.ToRgba(22, 0)` offline through a reflection probe: the vertex alpha byte
comes back 206, so the shader reads 0.8078, not 1.

So every cached surface was `1 / 0.8078 = 1.238x` too bright, at every hour, on every block. The
patch measures the light-path ratio at 1.29x once the stored colour's own 1.074x is divided out,
leaving about 4% unattributed and hue-neutral - consistent with vanilla's ambient occlusion and
shadow filtering, which cached ground does not have.

A static check was asserting the old derivation verbatim, so it failed on the fix. That is the
check working, but it had also been written from the same wrong reduction and would have locked
the bug in indefinitely. See G139.

### 3. Cause two: half of a two-tile texture is not the texture

`specialSecondTexture` on a TopSoil block is two tiles wide - the side fringe on the left, the top
face on the right - and `chunktopsoil.fsh` shifts one tile right for an upward normal.
`TryTopSoilColor` averaged the whole strip. The fringe is mostly transparent, so the *coverage*
came out far too low:

| coverage variant | whole strip | top tile |
|---|---|---|
| full grass | 0.687 | **1.000** |
| sparse | 0.537 | 0.843 |
| very sparse | 0.400 | 0.720 |

Coverage decides how much bare soil shows through, and that soil is the share the live tint must
not touch. So the mod believed roughly a third of every fully grassy block was untinted brown when
none of it is. The seasonal tint carries almost no blue, so that untinted third was where nearly
all the blue in distant grass came from.

Proved against the owner's own live cache rather than by inspection alone. The whole-strip formula
reproduces three stored colours to the byte - `soil-low-normal` 133,128,107; `soil-medium-normal`
128,120,98; `forestfloor-7` 131,124,102 - and the top-tile formula reproduces none of them. The
same code already knew about the two-tile convention: `ResolveBlockTexture` had taken the right
half since Session 71. Only the colour path had never been updated to match. See G140.

The two decisions are now made once and shared, because deriving the same fact twice is how they
came apart.

### 4. The cache repairs itself, which was not expected

The stored palette colour changes, and the owner's cache is 133 MB. A schema bump would have
wiped it. It turned out no migration was needed at all: `LodStore.Reclassify` already re-derives
flags, tint slot and colour from the live block on every section load and writes the section back
when anything changed, and `stableColorFor` returns 0 for block-entity blocks so a chiselled
block's genuinely per-position colour is preserved. Session 30 built that for G46 and it covers
this class of change for free.

### 5. Diagnostic: `.vhtint`, and what it eliminated

Two of the three residual suspects for the remaining grass blue were eliminated by instrument
rather than by argument. `.vhtint` takes one block's live tint apart and reports the three stages
separately, so a reading names the responsible stage instead of only confirming a disagreement:

- **A** what the shader multiplies by at this height, undiluted;
- **B** the same 64-position lattice re-run at *this* height, which removes the two-height
  interpolation from the picture;
- **C** the engine's own colour-map answer for this exact block, with no averaging.

The owner's reading on the test patch:

```
soil-medium-normal at 512443,118,513426 | sea level 110 | slot 11 of 20
  slot claimed by soil-medium-normal (this block)
  untinted share: this block R0.202 G0.145 B0.091, slot carries R0.202 G0.145 B0.091
  A shader uses here: undiluted R0.710 G0.602 B0.162
  B lattice average at this height:  R0.701 G0.571 B0.152
  C game's own answer, this block:   R0.659 G0.541 B0.125
  lattice spread at this height: R 0.608..0.816 G 0.463..0.694 B 0.078..0.220
```

Bucket sharing eliminated: the slot was claimed by this very block and carries its own share.
The two-height interpolation nearly eliminated: the ground is 8 blocks above sea level, blend
0.025, worth about 6% of the blue - and that 6% is not the interpolation but the low sample being
taken *at* sea level instead of at the terrain's height. The remaining +22% is the 56-block
lattice: blue runs 0.078 to 0.220 across that window while the true value here is 0.125, near the
bottom of a range spanning nearly 3x. Red and green sit mid-range and barely move.

Which half of the lattice is responsible is **not** established, and the session's first reading -
that it smears across a climate gradient - is only one of two. The lattice averages the season
map's per-block row as well, which is correct and is the reason it exists (G47), and the measured
spread is wider than the row range alone but does not say in what proportion. The two readings
call for opposite fixes, so the plan's Phase 0 is a cheap third measurement rather than a design.

A vs C is +30%, and the screenshot back-solve independently put the mod's tint at +31% blue
against vanilla's. Instrument and photograph agree, so the decomposition can be trusted.

### 6. Cause three: the stored colour was a sample, not an average

With the first two fixed, gravel was still about 12% bright and warm. `Block.GetColorWithoutTint`
answers with `BlockTextureAtlas.GetRandomColor`, which returns one of **thirty** pixels the atlas
kept, and those thirty are themselves a handful out of 1,024. Averaging 64 draws converges on the
thirty, not on the texture: granite gravel settled at 143,133,105 where the file's true average is
131.5,123.2,109.1.

The exact average was already being computed for the same texture - it is what the pattern
normaliser needs - and had simply never been wired to the colour. Taking it there is also the
safer of the two, because the shader draws `colour * (texel / mean)` and a colour and a mean
derived by different methods drift apart. See G141.

## Work narrative

Shipped in five artifacts. 0.3.167 added `.vhtint`; 0.3.168 fixed a defect in it that wedged the
owner's chat panel (see G142); 0.3.169 shipped causes one and two; 0.3.170 shipped cause three;
0.3.171 lowered the seam speckle default at owner direction.

The speckle change is a consequence of the fixes rather than a tuning preference. `BandSpeckle`
was 0.15, chosen in Session 65 when cached ground was 24% too bright with colours drawn from a
thirty-pixel sample; extra per-block variation was partly disguising a colour mismatch. With the
colour correct at source the same amount reads as noise rather than as texture, and the owner
called 0.08.

Also considered and deliberately not changed: `TryTopSoilOverlay` prefers `particletop` for the
texture pattern, which for `soil-*` is always `normal-top` regardless of coverage, so sparse and
very sparse grass are drawn with the full-grass pattern near the seam. The arithmetic cancels -
`vhBlockMean` is the mean of what is actually sampled either way - so this costs pattern accuracy
and not colour. Left open in TODO.

`dev/WIRE_HISTORY.md` was considered and not updated: stored palette colour *values* change, but
the blob format, its field meanings and the schema do not, and existing caches are corrected on
load by machinery that predates this session.

## Verification

Release build warning-free. The fast tier passes **5,964** assertions, up from 5,957: one static
assertion was corrected to the right derivation, and six new ones pin the tile-selection rule -
that a two-tile strip takes its top tile, that a square or tall texture is used whole, that both
survive atlas padding trimming the rectangle, and the two coverage values the split feeds.

Shader source re-verified pure ASCII per G10 (46,916 bytes, 46,916 characters, zero bytes above
127). No GLSL validator exists in the toolchain, so shader compilation remains unverified offline;
0.3.169 and 0.3.170 both rendered in the owner's client, which is stronger evidence than this
session could produce.

Every zip was integrity-checked, its `modinfo.json` version read back from inside the archive, and
copy-installed without touching any earlier artifact.

---

## Delivered

**0.3.167** - `.vhtint`, a diagnostic that takes one block's distant colour apart: stored colour,
untinted share as this block computes it against the share its tint slot actually carries, the
two-height table, and three separately labelled tint stages against the engine's own answer for
the same block. Never registers a slot. Aims at the block under the crosshair, falls back to the
block underfoot, accepts a block code.

**0.3.168** - fixed `.vhtint` wedging the chat panel. Chat text is parsed as markup and the report
contained `->`; a bare `>` is a closing tag with nothing open, and the scrollback is re-parsed on
every redraw so it kept erroring. Angle brackets removed from the report and stripped from the
chat copy on the way out.

**0.3.169** - the two proven fixes. `sunLightLevel` is read from `IWorldAccessor.SunLightLevels`
and `SunBrightness`, quantised the way the engine quantises it into the vertex byte, and applied
on the vanilla-ambient path only; `.vhlight sun` is the fifth switch beside the existing four.
`TryTopSoilColor` takes the top tile of a two-tile overlay, with `IsTwoTileStrip` deriving that
from the atlas rectangle's aspect rather than assuming it, shared with `ResolveBlockTexture`.

**0.3.170** - the stored colour comes from the texture's own alpha-weighted average instead of the
engine's thirty-pixel sample, wherever the block's colour texture stood and could be named.
`IsPlaceholderColor` extracted so the repair path and the exact-average path cannot disagree about
what a missing texture is.

**0.3.171** - `.vhband speckle` defaults to 0.08, down from 0.15.

**Tests** - `TopSoilColorChecks.TheTopTileIsTakenFromATwoTileStripAndNothingElse`, six assertions.
`StaticAssetChecks` light-term assertion corrected.

**Human-tested:** the owner accepted 0.3.169 ("things look SO much better") and 0.3.170 ("nearly
perfect"), and directed the 0.3.171 default from that state. 0.3.171 itself is built, verified and
copy-installed but has not been seen rendering.

## Decisions

**The sun level is a switch, not a silent correction.** It follows the four terms from 0.3.51 that
each carry their own `.vhlight` switch for the same reason: a single dusk-to-night sweep has to be
able to attribute what it sees. Uploaded as 1.0 when off, which is exactly the old behaviour and
needs no branch on the GPU.

**Applied on the vanilla-ambient path only.** The `sunColor * dayLight` fallback beside it is the
pre-0.3.51 model kept for comparison; scaling it would make it a third thing that was never
either.

**The tile-selection rule is derived, not assumed.** Taking the right half of a texture that is
not a strip would read a neighbouring block's tile out of the atlas. The rule is the atlas
rectangle's aspect, with a 1.5 threshold so a rectangle trimmed by padding still passes and a
square still does not.

**The exact average is refused where the texture cannot be named.** `MeanOf` falls back to the
atlas's four-pixel estimate, and four pixels is a worse average than the thirty already sampled,
so that case keeps what it had.

**The placeholder probe keeps its sampled value.** The missing-texture test compares against the
atlas's own average of `unknown.png`, which is a checkerboard, so the value it inspects has to be
sampled the way it always was. The exact average is taken only after that test declines.

**Rejected: bumping the schema to force re-capture.** It would have cost the owner a 133 MB
explored cache for a change that load-time reclassification already propagates.

**Rejected: correcting the sparse-grass pattern in the same build.** It is a real defect but it
moves pattern, not colour, and it belonged with neither of the two fixes the owner approved.

**Rejected: `.vhtint` calling `SlotFor`.** Registering a slot from a diagnostic forces an
off-cadence refresh and withholds every mesh using it until the table republishes. `PeekSlot`
looks up without inserting.

## Traps

**A derivation that cancels a factor must show it cancelling.** Trigger: reducing an engine
shader's arithmetic to a constant for a special case. Failure: 0.3.51 read the LOD case as "full
sky light" and cancelled `lightColor.a`; the renormalisation it relied on cancels the ambient
colour's mean, not the level, and sky light is 0.806 rather than 1. Worth 24% brightness on every
cached surface for four sessions. Safer: write the surviving factors out per term, and when a
check is added for the reduced form, derive the check from the engine source rather than from the
reduction. Promoted to G139.

**Two tiles in one rectangle.** Trigger: averaging a texture that the engine addresses by tile.
Failure: a TopSoil overlay is side-plus-top, the top face uses only the right tile, and averaging
both understated coverage by about a third - which is not a colour error but a *coverage* error,
and coverage decides the untinted share. Safer: derive tile-ness from the atlas rectangle and share
one decision between every path that reads the texture. Promoted to G140.

**Thirty pixels is not an average.** Trigger: using `GetColorWithoutTint` or `GetRandomColor` as a
block's colour. Failure: converging on the mean of thirty sampled pixels, 8% off in two channels
on granite gravel. Safer: read the texture. Promoted to G141.

**A bare `>` in chat text wedges the panel.** Trigger: writing a diagnostic report to chat.
Failure: chat is markup, an unmatched bracket errors, and the scrollback is re-parsed on every
redraw so it keeps erroring until the message scrolls away. Promoted to G142.

## Flagged and unverified

Judgement calls awaiting human review:

- **0.3.171's speckle default has not been seen.** The owner directed 0.08 from the 0.3.170 state
  but has not reported on a build carrying it as the default.
- **0.3.170 changed the stored colour of every non-TopSoil block in the game**, not only gravel.
  Most shift by a similar small amount; any block whose thirty-pixel sample was badly
  unrepresentative shifts more. Only stone and grassy soil were looked at.

Claims lacking the evidence level to treat as established:

- **The 4% residual on gravel is unattributed.** Ambient occlusion, shadow-map filtering and the
  atlas mip chain are the candidates; none is measured, and a screenshot cannot separate them.
- **The `.vhtint` C line is the CPU colour-map call, not the terrain shader.** The shader derives
  the same two coordinates itself and adds per-block value noise on both axes. C matched the
  screenshot's back-solved vanilla tint closely at this one position, which is encouraging and is
  not a general result - and it means the pre-existing seasonal-weight hypothesis in TODO cannot
  be tested by comparing A against C, because both go through `ApplyColorMapOnRgba`.
- **Nothing here is benchmarked.** No frame-time measurement supports any of it; the fixes are
  arithmetic in a shader and at palette-registration time, and none adds per-frame work.
