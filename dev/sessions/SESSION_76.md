# Session 76 — Climate that survives a restart, forests that turn unevenly, and what that cost

**Date:** `2026-09-03`
**Branch/commit:** `codex/seasonal-tint-blend-weight`, commits `2f839e0` through `f8d22f7`
**Mod version:** `0.3.185` - `0.3.194`; 0.3.185 and 0.3.193 human-tested and accepted
**Assist protocol / blob / schema:** `1 / 4 / 6`

## Context

Session 75 closed with the climate field accepted and the season column corrected. This session
retired the machinery that field replaced, made what it learns survive a restart, restored the
per-position seasonal variation that distant leaves had never had - and then spent its second half
paying for that variation, which cost the owner about 40 frames a second.

Nothing was launched by this session. Every measurement is the owner's in-game reading, his
screenshots, his client log read off disk, or the game's own assets and IL.

## 1. The height ramp is gone (0.3.185)

The 96-step ramp built in Session 74 was superseded by the climate field one session later and
survived only as the fallback for slots the field cannot finish - no paged climate map, or the
tropical seasonal weight this mod does not reproduce. Keeping both meant two ways to compute one
colour that both had to stay correct, and it had already cost a defect.

Those slots now draw the engine's own flat answer, which is what every build before the ramp drew
and is already the mechanism for a slot with no temperature. The distant colour table also settles
about seventeen times faster, undoing a slowdown the ramp introduced.

## 2. Remembered climate, and a handle that was never there (0.3.185, 0.3.190, 0.3.191)

`ClientWorldMap.GetClimate` answers only where the client has been sent map regions - about 1,000
blocks on arrival against 8,192 drawn - and returns a fixed constant elsewhere rather than failing.
The field therefore relearned everything from nothing every session. Climate answers are now kept
in the mod's own cache keyed on world position, in 32x32-cell patches, and nothing is stored for
ground the game never answered for: a guess is never saved as if it were data.

**It did not work, and the way it failed is G147.** `renderer.ClimateStorage = pipeline.StorageThread`
was assigned in `StartClientSide`; `LodPipeline` builds that thread in `Open`, which runs when a
world is joined. The handle was always null, so nothing crossed the disk in either direction from
0.3.185 to 0.3.190. Nothing failed - the consumer's null guard is correct, the in-memory half still
worked, coverage still grew as the owner explored, and he tested 0.3.185 and called it good. The
durable part, which was the whole point, was absent with no symptom at all.

The same null was also suppressing a crash. The flush added in 0.3.190 ran from the render path's
mesh clear, and on the world-leave path `pipeline.Close()` disposes the storage thread *before*
that clear is reached, so a live handle would have set a disposed `AutoResetEvent`. Fixing the null
alone would have converted an inert feature into an exception on every world leave. Both were fixed
together in 0.3.191: the handle is taken after `Open`, handed to before `Close`, dropped after, and
the write is out of the mesh clear entirely. `ClimateStorageIsWiredInTheRightOrder` asserts those
three positions and fails on the code as shipped in 0.3.185 through 0.3.190.

Found by reviewing this session's own work after the owner had already accepted the build.

## 3. Sparse grass: two independent defects (0.3.186, 0.3.188)

**The untinted share was shared too widely.** A grass-covered block is part grass and part dirt
showing through, and only the grass takes the seasonal colour, so how much dirt shows decides how
much of the block the tint cannot touch. Blocks were keyed on the MEAN of that share's three
channels rounded to eighths, which collapsed the base game's 29 top-soil blocks onto six groups and
lent `soil-medium-verysparse` (0.176, 0.125, 0.079) the share of `forestfloor-5` (0.188, 0.142,
0.107) - about 35% too much blue, and blue is where it shows because the seasonal tint carries
almost none. The key is now the exact byte triple the slot table publishes: 24 distinct shares
across 29 blocks, about two dozen slots of 64 rather than six, with overflow falling back to the
nearest registered share rather than to untinted.

**The top face drew the wrong texture.** The overlay on a grassy top face was taken from the
texture the game throws as walking particles, which for every soil block is the full-coverage one
whatever coverage the block actually has, so sparse grass wore the full-grass pattern. It now comes
from `specialSecondTexture`, which is what the game itself draws there. Colour was never affected -
only the pattern, and only close enough to the seam for a pattern to be visible.

## 4. A change that touches no edge is still a change (0.3.189)

G138, carried open since Session 72. `ReplaceColumns` returns which section EDGES a mutation
affected, and a block placed in the interior correctly answers `EdgeNone` - it changed the section,
but no neighbour can see it. Both callers read that zero as "nothing happened" and skipped
`MarkChanged`, so new runs and palette entries could enter resident RAM without dirtying the level-0
mesh, the persistent row or the mip obligation. A later edge mutation would flush the accumulated
state, which is why it looked intermittent rather than broken.

Content change and touched edges are now separate answers. `MarkChanged` already did the right
thing with `EdgeNone`; the defect was never reaching it.

## 5. Distant forests turn unevenly (0.3.187)

Owner-reported: trees just turning yellow in vanilla stayed flat green in distant terrain.

The game picks a ROW of the seasonal map per position, and for leaves it draws that noise at a
2-to-6 block scale where ground uses 12-to-24 - tree sized. During the autumn turn the sixteen rows
of `birchtint.png` run from (175,224,16), clearly green, to (177,156,42), clearly yellow. Distant
terrain drew the flat average of all sixteen, which contains no yellow at all.

Both are now carried: the shader picks a row the way the game does, using the engine's own
`noise3d.ash` rather than a transcription of it, and blends toward the average as one voxel grows,
because G47's reasoning is right once a voxel is a neighbourhood. Seventeen maps of 128x16 is
136 KiB. The tropical map is refused - the shader takes a different weight entirely for it - and
those slots keep the averaged colour.

## 6. What it cost, and two mistakes finding out (0.3.192, 0.3.193)

The owner reported a regression at a fixed scene: **315 fps before the feature, 275 after.**
`.vhtint leaves off` recovered most of it, which identified the feature but not the cost inside it.

**The cheap half.** `seasonAt` evaluated `valuenoise` four times per vertex, every frame, and both
noise terms depend only on world position - a per-vertex constant recomputed 275 times a second.
Two of the four built only the season COLUMN, which shifts the year by at most 0.05 and on average
0.025: about three columns of 128, a dither finer than the quad `tint` is interpolated across. It
now takes that jitter's mean, which is also the column `LodSeasonColor` centres its average on, so
the two ends of the blend stopped sitting up to three columns apart depending on the vertex. The
remaining two are no longer evaluated where `amount` is already zero - the fade ends at 8 blocks
per voxel and `columnBlocks` is a uniform outside the record path, so those draws were computing
the noise in full and multiplying by nothing. **Worth +17 fps.**

**The expensive half was reach, and that is G148.** The variation was drawn across levels 0, 1 and
2. A section holds the same 64x64 grid at every level - a coarse one costs the same per vertex, it
simply covers more ground - so the bill is set by how many LEVELS are included, not by how much
world they cover, which is the intuition the word "distant" invites. The owner's own log settled
it: 110 drawn sections as `L0:31 L1:57 L2:9 L3:1 L4:5 L5:3 L6:4`. **Level 1 alone had more sections
in view than level 0**, and fading out at 8 blocks charged 97 of 110 for a variation already down
to 0.57 strength by the third level.

`LeafSeasonFadeBlocks` now carries the reach as a uniform and defaults to 2 - level 0 alone, where
one voxel is one block and the noise is drawn at the scale vanilla draws it, beside the vanilla
trees it has to match. `.vhtint leafwide on` restores the old 8 so the two can be compared in place
at one scene without a rebuild.

Owner-measured at the same scene: **292 fps across three levels, 300 at one, 310 with the feature
off**, and no visible difference between one level and three. The feature now costs about 10 fps
rather than 40.

**Two reasoning errors, both the same shape - pricing a change without looking at what it applies
to.** Skipping the levels BEYOND the fade was called a large win before reading the census that
showed it was 13 sections, 12%; the cheap end of a level distribution is not where the sections
are. And when the +17 fps improvement did not visibly move the owner's frame counter, "I cannot
see it" was treated as "it is not there", which sent the next change chasing a texture fetch that
was not the problem. Both are recorded in G148.

## 7. Seam speckle back to 0.15 (0.3.194)

Reduced to 0.08 on 2026-08-31 because it was then the only variation cached ground had - one flat
colour per block type across a whole chunk - and against that it read as noise. Distant terrain now
carries real per-position colour variety before any speckle is added, and the owner reports the
original amount reading as texture again. Owner's call, and the reverse of the earlier one rather
than a repeat of it.

## 8. Priced and not built: leaves as cutouts

`dev/TODO.md` asked for a `cutoutfield --families` measurement before anyone extended G137's crop
fix to leaves. Run against the owner's live cache:

| | crops | leaves |
|---|---|---|
| share of level-0 sections | 42% | 78% |
| palette entries | 4,855 | 88,322 |
| opaque geometry | +2-4% | +19.95 / +20.24 / +22.02% |
| draw clusters | +0.00% | +0.00% |

Six to ten times what crops cost, landing where the entry predicted - X and Z walls +25 to +27%,
tops nearly free - because every pair of adjacent leaves in a canopy gains a wall between them.
Draw clusters do not move, which is usually the deciding number and is not here: this is vertex
memory and upload, not submissions. It does not fall off with distance either: +19.80% at level 1,
+19.27% at level 2, +15.99% at level 3. Flagging leaves at level 0 alone is therefore a real option
with a real saving. Nobody has yet observed a seam hole on a tree, and the harness reports the
entire cost without being able to report the benefit. **Owner's call, now with numbers.**

## Checks

21,016 assertions, zero failures; `dev/DocCheck.ps1` 1,976 checks pass. New this session: `InteriorChangeChecks`, `SlotKeyChecks`,
`ClimateStorageIsWiredInTheRightOrder`, `SeasonRowWiring`, `EveryDocumentedSwitchIsReachable`,
`TopFaceOverlayComesFromTheStrip`, and the seasonal fade asserted at each level rather than as a
source string, so its reach cannot drift silently.

`EveryDocumentedSwitchIsReachable` exists because removing a neighbouring switch cut from one
comment to another and silently deleted `.vhtint field on|off`. The build stayed green, the
property it set still existed, and the help text still advertised it; the only symptom would have
been a switch that quietly did nothing.

## State at close

Protocol/blob/schema remain 1/4/6. Nothing pushed. 0.3.194 is built, verified, packaged and
copy-installed but not yet seen.
