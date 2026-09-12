# Session 87 - distant structures drew white: chiselled blocks drawn as their most visible material

**Date:** `2026-09-12`
**Branch/commit:** `codex/water-depth-light`; started after Session 86's documentation `94e5761`; checkpoint 0.4.62 `e565558`
**Mod version:** `0.4.62`; built, verified, copy-installed and checkpointed. The owner ran it and reported "Excellent! Looks fantastic."
**Assist protocol / blob / schema:** `1 / 4 / 6` (unchanged)

> Session records are Tier 3 history. Write narrative as needed, but preserve the four required tail sections so future harvesting remains mechanical.

## Context and investigation

The owner asked what was next, then settled four open items in one message: 0.4.61 is the new
accepted baseline in place of 0.4.39; the three 0.4.55 visual checks are good (Redwood bark on all
four sides with a cut-end top; the captured snowy peak with murk on shows no dark rim; frozen lake
and glacier ice drawn as solid terrain looks right); the anti-aliasing slider ranges stay as they
are; and a deliberate look at 0.4.61 is low priority. He launched 0.4.61 at 12:47 anyway, and the
screenshots below come from it.

He then raised a long-standing problem: structures from other mods render with the wrong colours in
LOD. His pair `Pictures/Vintagestory/2026-09-12_12-48-45.png` (vanilla) and `_12-48-53.png` (LOD),
the same view eight seconds apart, shows a dark timber-and-iron mine headframe drawn almost white at
distance. He clarified that his test world has some mods disabled, whose blocks show as white
question-mark boxes in vanilla too, which is expected; the defect is blocks that look right up close
and go white only in LOD.

Evidence, all offline; no game was launched by this session:

1. The client cache `6bab2982-...aadd.db`, decoded with a blob-format-4 reader. Level-0 palette
   entries, near-white against real colour: microblock 2,035 / 352, chiseledblock 318 / 39,
   clutter 2,441 / 442, clutteredbookshelf 912 / 179, rocktyped-rubble-free 1,361 / 247,
   groundstorage 1,407 / 236, jonas-lamp 180 / 30. Columns holding ten or more blocks of these:
   2,078 white against 62 real.
2. The whites are exactly the missing-texture image's average in two forms: `0xFFFCFCFC`, the
   atlas's `0x00FCFCFC` repacked opaque, and `0xFFF9F9FF`, the file's own mean 255,249,249. Real
   entries of block-entity blocks carry alpha 00, which only the client's exact-position capture
   produces. White ones carry alpha FF, which only `StableColorOf` produces, and that is reached for
   a block-entity block only when a section arrives without colours: a server capture adopted by
   the client, or the colour-0 repair.
3. The server cache stores colour 0 for everything, as designed. The client and server caches hold
   nearly the same sections (5,307 and 5,319 at level 0), so most client sections were adopted.
4. Decompiled 1.22.7. `Block.GetColorWithoutTint` is `GetAverageColor(TextureSubIdForBlockColor)`
   after a decor check. `BlockMicroBlock.GetColorWithoutTint` answers from the block entity's first
   material and otherwise falls back to the block's own texture. `BlockShapeFromAttributes`
   (clutter) overrides only `GetRandomColor`. A chiselled block's contents are
   `BlockEntityMicroBlock.VoxelCuboids` (four bits per corner coordinate, the far corner minus one,
   the material index in the top byte) and `BlockIds`.
5. Better Ruins 0.6.3: 574 of 663 world-generation files use chiselled or clutter blocks (262
   chiselled); th3dungeon: 68 of 70. Its two mine schematics hold about 4,700-4,900 chiselled blocks
   each. The tower itself was not located in the cache: the client wrote no rows in that session and
   the tower's coordinates were not known.

Diagnosis, three faults together:

- The server half has no atlas and writes colour 0, and the client colours a received section by
  block type. A chiselled or clutter block's look lives in its block entity, so its type-level colour
  is only the placeholder (G181).
- The guard meant to turn the placeholder into a fallback compared the atlas's alpha-free value
  exactly, and `StableColorOf` always repacks opaque, so it never fired on that path (G180).
- A section keeps one palette entry per block id with the first colour seen, so the client's own
  later capture of the tower reused the stored white entry (G182). This is why standing beside it
  while vanilla drew it changed nothing.

The Cave Infix hardened rock also stored white (3,118 of 3,252) comes from a mod the owner has
disabled, and is correct.

## Work narrative

### 1. Choosing the fix

Weighed: repairing the guard alone, which turns chiselled blocks grey rather than their real
material; having the server record the material beside the chisel code, which changes the blob's
meaning; and recording the chisel as its material at capture, where both server and client hold the
block entity, so the palette only ever holds ordinary blocks. The third was chosen together with the
first. The owner asked for "the most visible part of the block, if possible", with the main material
as the fallback. It is defined as exposed surface: voxel faces touching air or the block's edge,
counted per material and summed per block. The engine's own majority-by-volume answer would draw a
skinned block as its hidden core, and a check shows the difference.

### 2. 0.4.62

- `Lod/LodChiselStandIn.cs` (new). `Collect` copies each `BlockEntityMicroBlock`'s cuboids and
  materials on the thread creating a capture job, only where the block read there is a chisel
  (`BlockMicroBlock`), with ineligible materials recorded as 0: snow, liquid, markers, flowers and
  block-entity blocks. `Resolve` and `StandInFor` compute the stand-in on the capture worker, cached
  by content per thread.
- `LodWorker.Capture` records a chisel as its stand-in while the block read is still that chisel, so
  runs split by material. `CaptureResult.StandInOriginals` keeps the chisel under each run top, and
  `LodPipeline.ApplyOneCaptureResult` reads the chisel's own flags there for the column's snow.
- Placeholder: `LodPaletteRepair.IsPlaceholder` compares RGB against the atlas average and the
  file mean, which is read when the level finalizes. Fallbacks: an entity-block answer that is itself
  the placeholder becomes neutral grey, and a real entity answer is kept; a plain block whose own
  answer is the placeholder keeps it, as the game draws a removed mod's block; a block with no colour
  texture stays grey as before. Stored placeholder colours on entity blocks are repaired on load
  (`ReplacePlaceholders`) and written back.
- The 30-second report gains `chiselled blocks: N captured as their most visible material, M with no
  material that can stand in, F column reads failed | R stored placeholder colours repaired on load`.
- The project references VSSurvivalMod (Private=false) for `BlockEntityMicroBlock`.

### 3. Verification

The fast tier passes 23,180 assertions with zero failures. `ChiselStandInChecks` adds 51: engine
encoding parity through `BlockEntityMicroBlock.ToUint`, exposed-face arithmetic, most-visible
against the engine's `getMajorityMaterial`, eligibility, aggregation by block, ties, the placeholder
forms, the load repair, and `LodWorker.Capture` end to end over `DispatchProxy` chunks. Reviewing the
diff before packaging found three defects in the first draft, all fixed: a lost space in the storage
stats line, a grey fallback that would have replaced a real entity answer, and a keep-the-placeholder
rule that would have drawn blocks with no colour texture (-1) white instead of grey.

Release build zero warnings. The zip has 38 entries and one DLL; `modinfo.json` and the DLL both
report 0.4.62, both new types were confirmed by decompiling, and it is 533,152 bytes with SHA-256
`C395F437CD77FF3B5CFF75735EF4C103359C672E2463B056C0F27F590539CA73`. The installed copy is identical.

### 4. Owner result

The owner ran 0.4.62 (the log shows it loaded at 14:09:31) and reported "Excellent! Looks
fantastic." The same log's 30-second report: 268 columns captured; **4,496 chiselled blocks captured
as their most visible material, 28 with no material that can stand in, 0 column reads failed, and
1,105 stored placeholder colours repaired on load**. The first missing-texture notice named
`caveinfix:hardenedrock-granite` and kept the game's own look, as intended. These counters are the
client's own captures; the server half's are not in the client log.

---

## Delivered

- The diagnosis, measured from the cache, the decompiled engine and the ruin mods' schematics.
- 0.4.62 (`e565558`): chiselled blocks as their most visible material; the placeholder guard and its
  fallbacks; the load repair; the stats line; 51 checks. Verified, copy-installed and checkpointed;
  nothing pushed.
- Owner decisions recorded: 0.4.61 is the accepted baseline; the 0.4.55 checks passed; the
  anti-aliasing ranges are kept.
- Documentation at the owner's request: this record and its index row; CHANGELOG 0.4.62; G180-G183;
  settled decision 36; a WIRE_HISTORY note; TODO, DONE and STATUS; a README line; a CLAUDE.md
  routing row; `dev/DocCheck.ps1` as recorded in STATUS (2,306 of 2,311, the five failures being
  the pre-existing `.testdata` log paths).

## Decisions

- A block whose look lives in its block entity is recorded at capture as the block it shows: a
  chisel as its most visible material. Server and client then agree, and nothing on disk or on the
  wire changes shape (settled decision 36).
- "Most visible" is exposed surface, summed per block, ties to the first material - not the engine's
  majority by volume.
- A stand-in must be something distant terrain draws as ground; otherwise the next most visible
  material; otherwise the chisel stays and takes grey.
- Blocks from removed mods keep the game's missing-texture look at distance (owner).
- A chisel's snow is decided by the chisel's own flags, not the stand-in's.
- Rejected: a second palette code naming the material (a blob meaning change), colouring the chisel
  by the game's first material (not what shows), and the guard fix alone (grey, not the material).

## Traps

1. **Trigger:** comparing a colour to the atlas's placeholder exactly. **Failure:** repacked colours
   carry an opaque alpha and the atlas's does not, so the fallback never fires. **Safer:** compare
   RGB, and include the file mean. G180.
2. **Trigger:** colouring a block-entity block by its type. **Failure:** the placeholder.
   **Safer:** read the entity at capture. G181.
3. **Trigger:** a later capture of a section that already holds a block type. **Failure:**
   `FindOrAddPaletteEntry` returns the stored entry with its old colour. **Safer:** correct colours
   on load, or change what the run records. G182.
4. **Trigger:** `Func<...>` in a file that imports `Vintagestory.API.Common`. **Failure:** CS0104.
   **Safer:** `System.Func`. G183.
5. **Trigger:** verifying a DLL's new types with `ilspycmd -l c`. **Failure:** it lists classes only,
   so a record struct looked missing and the install guard stopped a correct zip. **Safer:** `-l s`
   for structs, or decompile the type.
6. **Trigger:** bumping the version in `modinfo.json`. **Failure:** the project file's `<Version>` is
   separate; the static check caught the mismatch. **Safer:** bump both in one step.
7. **Trigger:** looking for the owner's screenshots in the game data folder. **Failure:** Session 86
   concluded he had none. **Safer:** they are timestamp-named under `Pictures/Vintagestory`.

## Flagged and unverified

Judgement calls awaiting owner review:

- Which materials may stand in, ties going to the first material, and neighbours not consulted.
- Clutter, bookshelves, ground storage and lamps take a colour from one of their own textures, or
  grey; not looked at specifically.

Claims without the required evidence:

- Whether the server half reads block entities on every capture path (savegame sweep, generation
  peeks): its counters are not in the client log.
- Structures cached before 0.4.62 turned grey on load and gain materials only when recaptured;
  `/vhrefresh` delivering refreshed rows to the client is unverified.
- The snow read for stand-ins (a snowy chiselled roof) is source-reviewed only.
- The cost of copying chisel contents where capture jobs are created is not measured.
- The owner's verdict covers what he looked at; which structures, and at what distances, was not
  reported.
