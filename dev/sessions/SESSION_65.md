# Session 65 — Seam blending, and real vanilla textures on cached terrain

**Date:** `2026-08-28`
**Branch/commit:** `master` at `21da6be` (0.3.127 baseline); this session's work is committed to a new branch at close
**Mod version:** `0.3.128` through `0.3.139`
**Assist protocol / blob / schema:** `1 / 4 / 6`

## Context and investigation

The owner opened the session wanting the vanilla-to-cached seam made imperceptible, with three
constraints carried from a previously discarded attempt: the transition must not read as a circle
with a hard line, it must begin at the vanilla seam and extend outward without taking vanilla's
ground, and hole protection must not regress.

An interview established the target and produced `dev/plans/PLAN_VANILLA_LOD_BLEND_BAND.md`. Two
things from that interview turned out to matter more than anything measured afterwards:

1. The owner named the defect a **texture** difference in the second round - "definitely seems hue
   and texture jump" - and the plan recorded it as such, describing a flat plate meeting speckled
   ground. Three subsequent implementations then treated it as a colour difference anyway.
2. The owner supplied the governing aesthetic rule after rejecting the earlier smooth ring: the
   old version failed **because its gradient was finer than one block**, so it read as a GPU effect
   painted over the world rather than as terrain. That became invariant 1 of the plan and shaped
   every later decision, including the 8-block cell size.

## Work narrative

### 1. The band

An offline measurement of the owner's screenshot put the seam step at R +21, G +12, B +22: cached
ground about 8% brighter than vanilla and less green. That number sized the band - roughly twenty
8-block steps, so about 128 blocks - and nothing else about it survived the session intact.

The mechanism did. `VanillaSeamDistanceField` records, per 8-block cell, the chamfer distance to
the nearest chunk vanilla owns, rebuilt from the ownership mask and uploaded beside it. The band's
shape is therefore vanilla's own footprint; there is no radius anywhere in it. Its inner edge is
shifted outward by `bandInnerBlocks` because ownership is deliberately denied for the outermost
chunk and a half, so the field's zero sits inside the seam a player can actually see - without that
shift the full correction lands under live vanilla where nothing can see it.

### 2. Three colour corrections, all rejected by play

- **Per-channel gain** (0.3.128), seeded from the measured ratio. The owner tuned it to look
  excellent on one terrain, then walked onto another where it was worse than nothing. Three
  red/green/blue multipliers *are* an assertion about what material is underfoot.
- **Darken and saturate** (0.3.130), derived by re-reading the same measurement as a shape rather
  than three constants - about 9% darker, about 25% further from grey, both operations on whatever
  colour is already there. Same outcome: right where tuned, worse elsewhere.
- **Per-block speckle** (0.3.130), one world-anchored random value per block, faded across the
  band. The owner liked it and it stays on, but he was explicit that it does not solve the problem.

The owner then closed the argument: vanilla terrain is never a single flat colour, no adjustment to
an average can meet it, and my measurements were not exposing what his eye could see. That
judgement was correct and is the pivot of the session.

### 3. Water, twice

Two separate defects, neither of them the blend band.

**Dry riverbeds.** Ownership asks whether the engine is drawing a chunk, and its *ground* answers.
The engine's water is a separate pass with a shorter reach, so there is a ring where vanilla draws
the bed and no longer draws the river, and inside it the mod suppressed its own water because
vanilla "owned" the cell. Both sides believed the other had it covered. Cached water is now never
suppressed by ownership, in the shader or in the whole-section skip; it can only ever add water.

**Seabed z-fighting.** A screenshot of a lake floor showed unmistakable depth fighting across a
broad band at the overlap distance. Cured by a constant sub-block sink applied to solid cached
geometry - deliberately constant, with no ramp and no edge, because the removed 8-block sink was
ramped and slid visibly as the player approached. Water surfaces are exempt so no lip can appear
across a flat river.

The severity difference is worth keeping: underwater the two fighting surfaces are a pale bed and a
dark water-tinted one, so every flipped pixel is maximum contrast, while the same fight between two
lit land surfaces is nearly invisible.

### 4. The unload hole

The owner found ground that stayed missing until he moved. Ownership is only withdrawn once loss is
*detected*, and vanilla unloads on its own schedule, so a window exists where neither terrain is
responsible. Cached terrain now takes ground back earlier by a tunable margin. The owner measured
16 blocks as better than 32 and that is now the default. This is a mitigation matched to a reported
symptom, not proof that loss detection is correct.

### 5. Real vanilla textures

The owner directed the heavy path, overruling a hypothesis of mine that the difference was
block-to-block variety rather than within-block texture. Source-tracing then made it much cheaper
than first estimated:

- The cache already stores block identity beside the averaged colour, and derived per-block data is
  already resolved on load without persisting (the seasonal tint slots do exactly this), so no
  re-capture and no format migration.
- At level 0 a quad's colour already **is** its block's colour, because greedy merging groups by
  palette entry rather than by shade. So a quad can carry a block slot *instead* of its colour in
  the same 24-bit field, with word 0's one spare bit saying which. Real textures therefore cost no
  extra memory at all, against the +33% first quoted.
- The engine will hand over each block's rectangle inside its own atlas, so cached ground samples
  vanilla's actual texels rather than an imitation.

`LodBlockTextureRegistry` holds that table; the renderer uploads it as a four-row float texture and
binds the game's atlas beside it. Emission is gated on a successful upload so every intermediate
state stayed shippable.

### 6. Four defects in the texture path, in the order they were found

1. **Red and blue swapped** across all of level 0. Stored palette colours pack red in the **low**
   byte; the new table read them as 0xRRGGBB. Tan sand became light blue. Predicted as "the failure
   mode with teeth" when the shared colour/slot field was chosen, then introduced anyway by
   assuming a byte order instead of checking it.
2. **The texture was invisible**, and turning it off changed nothing. Correct minification averages
   a shrunk tile down to its own mean, and since the texture is applied as variation *around* the
   block's colour, variation-equals-mean means multiply-by-one. Sold to the owner as a safety
   property one build earlier; it is actually the central obstacle. `.vhtexture detail` now samples
   sharper than the footprint warrants to recover it.
3. **The wrong face.** `TextureSubIdForBlockColor` answers "what colour is this block", not "what
   does its top look like", and for grassy soil it is the side. Adopted because it cut no-texture
   refusals from 976 to 46 - a real gain - without checking which face came back.
4. **The wrong overlay.** `specialSecondTexture` is the *side fringe*: measured off the game's own
   `normal.png`, alpha 255 for eight rows then 128 for twenty-four. Flat on a top face that draws
   every block half grass and half dirt, which is exactly what the owner reported twice. The game
   keeps a fully-opaque `normal-top.png` for top faces, named under the block's `particletop` key.

Reading the game's texture files took two minutes and was unambiguous. It should have happened
before the first overlay build rather than after the second complaint.

### 7. Near-field suppression removed

A 48-block radius had suppressed cached terrain around the camera since early in the project. It
guarded against coarse geometry appearing at arm's length in cells vanilla never proves - typically
underground - but it also hid cached terrain from a player who turns the vanilla view distance down
far enough to want to look at it, which is the direction the whole mod exists to enable. Now a knob
defaulting to zero. Checks were added asserting the per-cell mask and whole-section skip survive,
because the owner specifically warned against taking the masking with it.

---

## Delivered

**Source, new:**

- `VintageHorizons/src/Render/VanillaSeamDistanceField.cs` - 8-block chamfer distance to vanilla's
  footprint, wrapped-ring addressing mirroring the ownership mask.
- `VintageHorizons/src/Render/LodBlockTextureRegistry.cs` - block to texture slot, atlas rectangle,
  averaged colour, normalisation mean, and grass overlay rectangle.

**Source, changed:** the packed quad format (textured flag in word 0 bit 31, slot sharing the colour
word), the mesher and cluster encoder, the palette/snapshot/store/pipeline chain carrying a derived
texture slot, both shader bodies, and the renderer.

**Commands:** `.vhband` (on/off/shape/speckle/width/step/inner/strength/darken/saturate/sink/overlap),
`.vhtexture` (on/off/strength/detail/mips), `.vhnear`. `.vhband` and `.vhtexture` with no argument
write their state to the client log, because game chat cannot be copied out.

**Tests:** `SeamFieldChecks` (new suite, 26 assertions), packed-quad assertions for the shared
colour/slot word and the table's channel order, static checks pinning band wiring, block-texture
wiring, the water exemption, the sink's single constant application, and the survival of per-cell
ownership. 5,450 assertions at session open, **5,574** at close, zero failures.

**Playable artifacts:** 0.3.128 through 0.3.139, each built, verified and copy-installed. 0.3.139 is
the current build.

## Decisions

- **The band is derived from ownership, never from a radius.** Its shape is vanilla's own footprint
  and it inherits the chunk-stepped outline for free.
- **8-block cells.** A quarter of a vanilla chunk: fine enough to read as a smaller continuation of
  the game's blocky idiom, coarse enough to still read as steps, and never finer than one world
  block. There is deliberately **no continuous option** in the step-size knob.
- **A quad carries a block slot instead of its colour**, sharing one field. Chosen over a 16-byte
  quad because at level 0 the colour is already per-block data. Blocks whose colour is genuinely
  per-position - chiselled, with a block entity - keep the colour form.
- **The texture is variation around the block's colour, never the colour itself.** This is what
  makes it impossible for a texture to shift a block's tone, and it is why the grass composite the
  mod already computes correctly survives untouched.
- **The overlap margin is a knob at 16 blocks**, the owner's measured value.
- **Near-field suppression defaults to zero**, at owner request, with the old 48 one command away.
- **Rejected:** per-channel colour gain, darken/saturate as a general correction, and the hypothesis
  that the seam difference is block-to-block variety rather than texture. The last was overruled by
  the owner on domain knowledge and he was right.

## Traps

- **Trigger:** putting a stored palette colour into a new consumer. **Failure:** assuming 0xRRGGBB;
  red is the **low** byte, so red and blue swap and tan surfaces turn blue. **Safer:** decode
  through one shared helper tied by a check to the encoder the shader reads.
- **Trigger:** wanting "the block's texture". **Failure:** `TextureSubIdForBlockColor` is the
  block-colour texture, not the up face; for grassy soil it is the side. **Safer:** ask by face
  name and fall back to the generic texture only for coverage.
- **Trigger:** compositing a grass overlay. **Failure:** `specialSecondTexture` is the side fringe,
  opaque at the top and half-transparent below; flat on a top face it draws half grass, half dirt.
  **Safer:** use the block's `particletop` texture for top faces, and read the actual PNG's alpha
  profile before believing any of this.
- **Trigger:** sampling a real texture on distant terrain. **Failure:** correct minification averages
  it to its own mean, so a mean-normalised texture becomes a multiply by one and the feature appears
  to do nothing - including when switched off. **Safer:** choose the mip level explicitly.
- **Trigger:** normalising by an average. **Failure:** normalising by a mean that describes
  something other than what is actually sampled - a grass composite while sampling bare soil -
  shifts a channel hard; blue inflated about threefold. **Safer:** the mean must describe the exact
  sample.
- **Trigger:** measuring two patches in a screenshot. **Failure:** three times this session I drew a
  confident conclusion from patches whose geometry, face orientation or block variant I had not
  established - a side face against a top face, and a young grass block against mature terrain.
  **Safer:** have the owner identify which region is which before measuring, and prefer the game's
  own asset files to inferring from pixels.

## Flagged and unverified

**Judgement calls awaiting human review:**

- Near-field suppression at zero. The artefact it guarded against - coarse cached geometry at arm's
  length underground - has not been re-tested.
- The overlap margin at 16 blocks closes the reported hole; the underlying loss-detection latency
  is unproven.
- Grass ground keeps the established olive tone, because the distant colour pipeline blends soil and
  grass by the **side** texture's coverage. Deliberately not touched.

**Claims lacking the evidence level to be treated as established:**

- No benchmark was run this session. The band is *expected* to be nearly free - one texture fetch
  inside it and a bounded field rebuild - and block textures add a fetch on textured fragments. Both
  are unmeasured.
- Only the packed render path draws textures. The legacy and non-packed indirect paths are unchanged
  and untested against this work.
- Water and thin cover are untextured by design; cliff faces use the up-face texture.
- 46 blocks resolve no texture and 241 keep per-position colour; neither figure has been examined
  for what ground it actually covers.
- **Terrain Slabs.** The owner reports that this world was generated with a mod producing half-block
  terrain slabs, and that Vintage Horizons handles them poorly. Not investigated; carried to a fresh
  session as the next piece of work.
