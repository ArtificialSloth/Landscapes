# Handoff — the distant-water seam band

**Written:** `2026-09-04`, at the close of Session 78
**Branch:** `codex/water-depth-light`
**Head:** `e6bfbe9` (documentation); last source commit `c3966ac`
**Installed and played:** `0.4.10`
**Rollback point:** branch `checkpoint/pre-water-depth` at `bc3a1c0`, which is the state
before any water work in this session

Read `dev/sessions/SESSION_78.md` first for the full arc. This document is the short version
plus everything needed to continue without re-deriving it.

---

## The problem, stated precisely

A **chunk-aligned band runs along the boundary between vanilla's water and cached water**. It
is brighter than the water on either side of it and it is not attributed.

Measured off owner screenshots taken on 2026-09-04, in the game's own screenshot folder under the user profile's `Pictures/Vintagestory`:

| sample | luma |
|---|---|
| cached water beyond the band | 80 |
| **the band** | **93** |
| vanilla water approaching | 82, falling to 55 nearer the camera |

Two properties that constrain any explanation:

1. **It sits exactly on the ownership boundary.** Confirmed with `.vhpaint on`: the luma peak
   coincides with the transition from 0% to 100% mask coverage.
2. **It overshoots AWAY from vanilla in both directions.** In a dark scene it measured 15
   against 23 beyond and 35 approaching; in a bright scene 90 against 82 and 60. So it is not
   a blend between the two surfaces — it is cached water with something extra applied, or
   something of vanilla's reacting to us.

The owner's own reading, which is the most recent and least tested: *"it was us seeing through
vanilla water into LOD chunks and looking at the seabed through the vanilla water."*

---

## What has been ruled out, and how

| Candidate | Status | Evidence |
|---|---|---|
| The seam blend band | **Ruled out by source** | `!translucent` gated in `lodterrainbody.fsh`; land only, never water |
| Fog interpolated per vertex | **Measured, too small** | Our quads span tens of blocks where vanilla's water has a vertex per block, but the worst-case error is 0.022 fog units (~1 luma), peaking where `min(250, depth)` clamps. About a tenth of the band |
| Our wave strip | **Tested negative** | `.vhwater wave 0` |
| Our depth shading | **Tested negative** | `.vhwater depth off` |
| The blend pipeline | **Tested negative** | `.vhblend off` |
| Our water opacity | **Tested negative** | `.vhwater fresnel off` |
| Water walls at uncaptured edges | **Tested negative** | `.vhwater walls off`, built for this purpose in 0.4.10 |
| Vanilla's own foam firing on our depth writes | **Argued, never tested** | Superseded by the owner's reading; see below |
| **Our seabed seen through vanilla's water** | **UNTESTED — the live candidate** | See next section |

The owner confirms he restarts the game for each build, so those negative results are against
the build that carried the switch.

---

## The live candidate, and the one-command test

Our seabed carries a darkening that vanilla's does not:

| depth | vanilla's bed | ours at `clarity 10` |
|---|---|---|
| 4 blocks | 0.85 | 0.57 |
| 8 blocks | 0.70 | **0.32** |
| 12 blocks | 0.55 | 0.17 |

Where vanilla's water is transparent over **our** bed, ours sits beside vanilla's own bed under
the same water. Those should be identical and are not. That would produce a hard-edged band
wherever the two beds meet, and no water switch would remove it because it is the ground.

**The test:**

```
.vhwater depth on
.vhwater clarity 0
```

`depth on` is required first. The bisection left `.vhwater depth off` **saved in the owner's
config**, which disables the whole absorption table and therefore makes `clarity` a no-op — he
reported "clarity has no effect on visuals anymore" and that is why. The command does append
the resolved state to its reply, but it is easy to miss.

If the band shrinks or goes, the conclusion is that the bed must match vanilla's bed exactly
and must never be used to compensate for anything on the water side.

---

## The known-bad design that is driving this

`WaterClarity` **has no counterpart in the engine.** Vanilla's bed carries the sunlight that
reached it and nothing else; everything that resembles extinction comes from a transparent
surface blended over that bed, per pixel, with an alpha that already depends on view angle.

This term was invented to compensate for `WaterMaxAlpha` being capped below what vanilla's own
formula gives, so the bed would show through at range. Being baked per block, it is a function
of depth alone. The real quantity is depth divided by the cosine of the viewing angle.

**The owner found this from the outside:** a clarity that matches at one render distance stops
matching at another. That is the missing variable and it is not fixed.

### Doing it properly

Carry the **depth** to the fragment instead of the factor, then the shader can raise the baked
value to the power `1/cos(theta)` — the identity `exp(-d·k/cos) == (exp(-d·k))^(1/cos)` makes
this one `pow()` once the depth is available.

The packed record has no free bits (`LodPackedQuadFormat`: word 0 is 28 bits of coordinates
plus 3 face bits plus the textured flag; word 1 is two 16-bit quarter-block heights; word 2 is
a 24-bit colour/slot plus the alpha byte). **The spare room is in the texture-slot field**:
`MaximumTextureSlot` is 24 bits and real slot values are twelve — the live log reads
`967/4096 textured blocks`. Depth needs five bits.

Doing that would also undo a loss taken in 0.4.4: submerged quads currently give up their block
texture entirely, because a textured quad spends word 2 on the slot and the vertex shader
recovers the colour from the block table by it, so a colour written there is read as an index
and discarded (G154). Packing depth into the slot's spare bits would restore underwater
textures AND give the shader the depth. It is a shader change on every render path.

---

## Two attempts that failed, so they are not repeated

**0.4.8 — matching vanilla's formula exactly.** Ceiling to 1.0 and clarity to 0, on the
argument that vanilla's fresnel saturates at range. The argument is sound and ignores that at
that range **vanilla draws no water at all**, so a flat opaque sheet has nothing to agree with
and sits beside vanilla's near water, which visibly grades. Measured: cached water flat at luma
80 against vanilla running 82 down to 55. The owner reported it worse and it was reverted in
0.4.9. **The owner's eye is the acceptance evidence for anything visual.**

**A blanket water overlap.** Letting cached water draw one chunk inside vanilla's territory to
fill gaps. The owner rejected it — *"we've tried it before"* — and the ownership test's own
comment records why: a previous attempt used a RADIUS and cached water drew into chunks vanilla
was already drawing. The comment states the requirement for any future attempt: *"the answer
will be a better ownership signal, never a radius."*

**A retracted finding.** A screenshot showing a wide expanse of bare seabed was read as a
rendering defect and traced to vanilla fading its own water out over the last fifth of the view
distance (G124 returning). The owner then said the terrain simply had not finished loading when
he took it. The finding was retracted and the half-built change discarded. See G156.

---

## Where things are

| What | Where |
|---|---|
| Depth absorption table | `LodTerrainRenderer.WaterAbsorptionTable()` |
| Baking into the mesh | `LodMesher.SubmergedColor`, `SubmergedBlocks`, `WallSubmerged` |
| Merge keys carrying depth | `LodMesher.EmitHorizontalGreedy` / `EmitVerticalMerged`, field `Submerged` |
| Texture-slot suppression | `LodMesher.TextureSlotFor` |
| Wave strip on the chunk grid | `LodMesher.SeamCellFor`, field `SeamCell` |
| Wave displacement | `lodterrainbody.vsh`, `applyLiquidWarping` + `vhOceanity` / `vhWaveReach` |
| Oceanity sampling | `LodTerrainRenderer.SectionOceanity`, via `IMapRegion.OceanMap` |
| Water ownership test | `lodterrainbody.fsh`, `vanillaOwnsCell`, green channel |
| Water footprint | `VanillaReadinessMask.RebuildWaterFootprint` |
| Water walls guard | `LodMesher.CollectSide`, `SuppressUnknownWaterWalls` |
| Offline geometry harness | `tests/VintageHorizons.Checks/WaterDepthField.cs` |

**The harness** measures against the owner's real cache with no game process:

```
dotnet run --project tests/VintageHorizons.Checks/VintageHorizons.Checks.csproj \
  --configuration Release -- waterdepthfield --samples 120
```

It reports the depth histogram, the geometry cost of depth shading, the cost of splitting water
at several patch sizes, and the cost of the chunk-grid strip at several widths. Note that the
owner plays on that cache, so figures taken days apart are **not** a controlled pair — the same
section keys hold different contents.

---

## Environment notes that will otherwise cost time

- `VINTAGE_STORY` must be set to `%APPDATA%\Vintagestory` for any build.
- Git refuses without `git -c safe.directory="$PWD"`; the `.git` directory has another owner.
- PowerShell 7 is at `C:\Program Files\PowerShell\7\pwsh.exe`, **not on PATH**.
- Python here defaults to cp1252 — always pass `encoding="utf-8"` explicitly.
- Files are CRLF; a Python edit that matches on `\n` will silently fail to find its anchor.
- There is **no offline GLSL validator**. A shader compiling is only proven by the game loading.
- The bench harness deploys **Debug** artifacts and refuses stale ones.

## Working agreement that matters here

- **Never launch the game without asking**, including a sandbox bench run.
- Bump the patch version, package, verify and copy-install to `%APPDATA%\VintagestoryData\Mods`
  whenever source is ready for testing. **Copy only** — never remove an older zip.
- The owner is not a developer. Lead with what changed and what it means, then a sentence or
  two of mechanism.
- Say plainly when something is unproven, and do not present an argument as a measurement.
  Three attributions in Session 78 were argued before they were evidenced; one shipped as a
  build the owner reported worse.
