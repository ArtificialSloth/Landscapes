# Session 77 — Where the 0.3.127 → 0.3.194 frame time went, and giving it back

**Date:** `2026-09-03`
**Branch/commit:** `codex/vertex-tint-cost`, from `checkpoint/pre-vertex-cost-fixes` (`1542664`)
**Mod version:** `0.3.195`-`0.3.197`, promoted to `0.4.0` at owner direction
**Assist protocol / blob / schema:** `1 / 4 / 6`

## Context and investigation

The owner reported 360 fps at 0.3.127 and 285 at 0.3.194, same scene, same vanilla
settings — a 0.79 ms regression across 28 commits — and said the visual gain did not look
like it was worth that. He then ran the feature switches himself and found about half of it:
`.vhtint field off` bought 30 fps, `.vhcutout on` bought 10, and water and the seam band
bought nothing. A second round with `.vhblend off` and `.vhslabs off` moved another 18
between them, but from a baseline that had drifted from 320 to 303 between readings, and he
said plainly that the benchmarking was not clean.

He was right, and the reason matters: **the baseline drifted between readings by more than
the effects being measured.** An FPS counter read by eye between toggles cannot resolve a
40 µs feature in a 3.5 ms frame. Every number in this session comes from the harness
instead.

Out of scope: the owner's own world. Everything measured here is the sandbox at one seam
viewpoint, which runs roughly 2.9× lighter than his scene. Proportions transfer; absolute
microseconds do not.

## Work narrative

### 1. Source trace first, and what it ruled out

The per-frame CPU structure is unchanged between the two versions: same two draw passes,
same six per-section uniforms, same draw submission, and every added CPU path
(`CollectRangeLosses`, the climate field refresh, the seam field build) is interval-gated or
amortised across frames. What changed is the shaders. The fragment body went from 301 lines
to 836 and the vertex body from 191 to 554, and the vertex tint went from two uniform-array
reads to about a dozen scattered dependent texture fetches.

### 2. Making one launch answer the whole question

`bench-windows.ps1` measures one configuration per run. Eight configurations at ~8 minutes
each was the wrong shape, so the bench mod's route format gained a trailing `! <command>`,
dispatched when a waypoint is entered and before its settle window. The harness's own
stability criterion then absorbs whatever the command starts — a bounded re-mesh, a table
upload — and the measured window sees only the settled result. Client `.` commands go
through `ExecuteUnparsed`; `SendChatMessage` reaches the server, which drops them silently.

That is what makes a one-session A/B trustworthy: every state is judged by the same
criterion instead of by a human watching a counter.

### 3. Three runs

**Run 1 was invalid and is kept as a control.** The harness pins
`VINTAGEHORIZONS_CHUNK_MASK=0` unless `-ChunkMask` is passed, so it measured the radial
handoff path with no ownership mask and no seam field — not what the mod ships. Reading
`mask off` in the readiness line is what caught it. A grep over the same line then produced
the opposite error: `-oE "mask [a-z]+"` matches `paints mask off`, the debug-paint field, and
briefly said run 2 had no mask either. It did. Read the field, not a substring of the line.

**Run 2** (mask on, band active, 118×118 seam field cells, 743 µs build) is the valid sweep.
**Run 3** built `0.3.127` in a detached worktree, staged its Debug output over the deploy
directory, and measured it at the same viewpoint.

| Build / state | frame | GPU p95 | CPU callback |
|---|---|---|---|
| 0.3.127 | 1.64 ms | 450 µs | 1250 µs |
| 0.3.194, everything on | 1.91 ms | 725 µs | 925 µs |
| 0.3.194, five features off | 1.75 ms | 600 µs | 1250 µs |

**+270 µs of frame time against +275 µs of GPU time. None of it is CPU** — the mod's own
main-thread callback is *lower* at 0.3.194. Debug builds inflate the CPU side and not the
GPU side, which only strengthens that conclusion.

Cumulative toggle deltas: band 20 µs, blend 40, climate 80, slabs 10, cutout 10 — 160 µs of
the 270. **The remaining 110 µs was reachable by no switch at all,** which is exactly the
half the owner could not find.

### 4. What the residue was

Three costs that are the *shape* of the new vertex shader rather than any feature:

1. The tint fallback moved from a uniform array to a texel, so every vertex paid one
   scattered dependent fetch where 0.3.127 paid nothing — on 3.2 M vertices per frame.
2. Three unpacked block-table rows travelled as flat varyings, eleven floats per vertex,
   written on **both** branches and so paid in full even with `.vhblend off`, for data only a
   fragment inside the blend band ever reads.
3. Ownership testing gained up to four texel fetches at chunk boundaries plus the plane
   arithmetic on every fragment.

### 5. The fixes, and what they measured

The two hot slot-table rows became uniform arrays, mirrored from the same packed row the
texture is built from — one source, two shapes, because a slot reads its fallback from one
and its season blend from the other. The block table's rows moved to the fragment stage
behind a single `flat out int` slot. The climate lookup gained a fade by voxel size.

| | frame | GPU p95 |
|---|---|---|
| 0.3.194 | 1.91 ms | 725 µs |
| 0.3.195 | **1.81 ms** | **625 µs** |

100 µs of 270 recovered, with **identical geometry drawn** — 3.204 M vertices/frame in both,
so this is less time per vertex and not less drawing. And with the five features off, 0.3.195
measures **1.64 ms: exactly 0.3.127**. The residue is closed; what remains above baseline is
now only the features themselves.

### 6. The settings window

Every measured feature became a saved setting and a row in `.vhconfig`. They had been
session-only, which is the wrong default for the people they exist for. The `.vh*` commands
that set the same values now save through the same path, so the window and the commands
cannot disagree across a restart; the numeric dials (`.vhwater glint <n>`) deliberately do
not save, because coarsening a typed fraction to on/off on the way to disk would be a silent
change of value.

The first attempt used two tabs and printed each description under its title. Two owner
screenshots killed it: the tab strip clipped both labels to "Det" and "Visual fe", and a
medium-weight title is taller than the box it is given, so every row overran its own
description. One page, titles only, descriptions on hover.

The owner supplied the final titles and descriptions, and one of them changed a meaning:
"Close Biome Colors" is L0 and "Far Biome Colors" is L1–L6, where the implementation had
split at L0–L2. `CloseClimateTintRange` is therefore 1 and not 4, and his description is
accurate as written. They are two ends of one shader threshold, so Far implies Close.

---

## Delivered

- **Bench harness** (`1542664`): route lines carry `! <command>`; two routes, the toggle
  sweep and a plain steady-viewpoint route for measuring a build rather than a switch.
- **0.3.195** (`cc86088`): the two hot slot-table rows as uniform arrays, seeded at
  construction as well as at refresh; block-table rows moved to the fragment stage behind one
  flat slot varying; `climateFadeBlocks` and `.vhtint climatewide`.
- **0.3.196** (`78df0e5`): every visual feature saved to config and exposed in `.vhconfig`;
  `LodVisualSettings` with Performance/Balanced/Quality presets; startup restore; on/off
  commands persist.
- **0.3.197** (`8308141`): one page, hover tips, preset drop-down, owner's titles, Close/Far
  split at L0 against L1–L6.
- **0.4.0**: version strings only, at owner direction.
- 21,578 assertions, zero failures.

## Decisions

- **Two rows in uniforms, two left as texels.** Four arrays of 64 `vec4` is 1024 float
  components, exactly the guaranteed vertex uniform minimum with nothing left over. The two
  rows every vertex reads are worth the budget; the two read only inside the climate branch
  are not.
- **The block table moved to the fragment stage rather than being compiled out.** A
  compile-time variant would have removed the varyings only when textures were off; moving the
  reads removes them always, and puts the fetches on the side that actually asks.
- **Far Biome Colors defaults OFF.** It is the single most expensive item here and not paying
  for it is most of what 0.3.195 bought. Quality turns it on. Flagged for the owner.
- **`.vhcutout` is not in the window.** Turning it on is a small win but it is the setting
  that opened holes at the seam; a known-defective option in a player-facing window generates
  bug reports rather than help. It stays a command.
- **Run 1 kept rather than deleted.** It is a valid measurement of the radial handoff path and
  the honest record of a configuration error.

## Traps

- **A harness switch can pin a default off.** `bench-windows.ps1` writes
  `VINTAGEHORIZONS_CHUNK_MASK` in both directions, so a run without `-ChunkMask` measures the
  radial path, not the shipped one. Read the configuration back out of the run's own log
  before trusting a sweep. Promoted as G149.
- **Grepping a substring of a telemetry line can answer about a different field.** `mask
  [a-z]+` matched `paints mask off`. Anchor on the field's own delimiters.
- **A derived mirror must be seeded wherever the original is.** The uniform arrays were filled
  by `BuildSlotTable`, which does not run until the first refresh, so the shader would have
  read a black fallback and climate page zero — a real page — on every world load. Caught by
  the static check written for the change. Promoted as G150.
- **Debug is what the bench deploys.** CPU phase numbers from a bench run are unoptimised;
  GPU timer numbers are not. Do not compare a bench CPU figure against a release expectation.

## Flagged and unverified

Judgement calls awaiting human review:

- **Far Biome Colors defaulting off**, and with it a possible colour step at the L0→L1
  boundary (~1,024 blocks on default thresholds) where Close is on and Far is off. This is the
  one visual consequence of the fade that needs an eye on a horizon with a biome edge on it.
- The three preset compositions, and the coarse cost words on each row.

Claims that lack the evidence level to be treated as established:

- **The settings window has never been seen running.** 0.3.196's layout was wrong in a way
  only a screenshot caught, and 0.3.197 moved everything. Source-traced and harness-tested
  only.
- **The fixes are measured in the sandbox, not in the owner's scene.** His 793 µs regression
  is 2.9× the sandbox's 270 µs; the 100 µs recovered should scale, but that is inference.
- 0.4.0 is source-identical to 0.3.197, which is **not human-tested**.
