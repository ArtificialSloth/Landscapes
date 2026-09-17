# Session 97 — Farseer persistence, tuning and lightweight joins through 0.4.94

**Date:** `2026-09-16`
**Branch/commit:** `codex/water-depth-light`; playable checkpoints `c66e0f6` (0.4.88), `bf2b88f` (0.4.89), `c49edae` (0.4.90), `37d3c1d` (0.4.91), `c7924c0` (0.4.92), `e2f6513` (0.4.93), and `0df61f4` (0.4.94). This documentation pass is uncommitted at close.
**Mod version:** `0.4.94`
**Assist protocol / blob / schema:** `1 / 4 / 6`. Silhouette transport generation and both silhouette stores remain schema/version `1`; no compatibility meaning changed.

> Session records are Tier 3 history. Write narrative as needed, but preserve the four required tail sections so future harvesting remains mechanical.

## Context and investigation

Session 96 left P4 ready for its first owner run and P5 waiting behind it. The owner then tested the
feature repeatedly and supplied screenshots and direct observations while away from the development
machine. The feature drew and persisted, but the first passes exposed four separate classes of work:

- the client needed its own durable tile cache for instant rejoin;
- the Settings scale needed more room, consistent typography and clearer distance labels, and the
  feature should be called **Farseer** to credit the mod whose MIT-licensed work inspired it;
- the sampled outline was too angular and detailed, and needed genuinely effective tuning;
- changing reach or shape rebuilt too much on the game thread, while the join alternated between a
  small sky gap and visible overlap.

The owner also observed roughly 550 to 450 FPS with the early 8 km silhouette pass. That was an
informal scene observation, not a controlled A/B or GPU timing capture, but it established the
product direction: Farseer geometry is intentionally extremely simple, and the feature should have
as little performance impact as feasible.

No game or server was launched by the assistant. Every visual statement below comes from the
owner's runs; source, build and harness evidence are identified separately.

## Work narrative

### 1. P5 client persistence (0.4.88–0.4.89)

0.4.88 added `<savegame>-silhouette.db` on the client. It opens only after silhouettes are enabled
and the connected server offers them, reads nearest stored tiles through the store's bounded worker,
and closes with all RAM/worker state when the feature is switched off or the world changes. Network
replies remain authoritative for the current session; a stored revision never prevents a server
whose tile revision sequence restarted from replacing it. Client saves replace exact authoritative
rows, whereas the server store retains its merge behavior.

0.4.89 closed a startup ordering hole: tile replies can arrive before `LevelFinalize` supplies the
world-specific store path. Those already-confirmed tiles are now backfilled when the store opens.
The owner subsequently reported that leaving and rejoining immediately reproduced the prior
Farseer terrain. This is human evidence for the visible persistence result, not yet a cold-process
or dedicated-server restart matrix.

### 2. Settings, naming, reach and first join smoothing (0.4.90–0.4.91)

The visible Settings tab was renamed from **Silhouettes** to **Farseer** at the owner's request. The
General threshold scale gained vertical space, uses the vanilla tab's font/size consistently, and
puts each numeric distance on a second line under `L1`–`L6`. The Farseer reach handle was repaired:
changing it now reaches the live client and server request path instead of remaining effectively at
8 km.

The join moved to live cached edge heights and eased back toward the sampled field across the
adjoining cell. The height fill gained broad feathering so isolated peaks contribute to a rolling
profile rather than a chain of sharp angular points. 0.4.91 increased that feathering after the
first pass remained visibly too sharp.

The owner asked that most remaining player-facing “Silhouette” names eventually become “Farseer”
and that Farseer's MIT license receive proper bundled credit. This session changed the visible tab
and shader/source attribution comments; the broad internal/config/command rename and separate
license inclusion remain explicit follow-up work, not silently completed scope.

### 3. A real geometry/shape control (0.4.92)

The earlier look controls did not materially change the outline. 0.4.92 added a saved **Outline
detail** dial that changes which worker-built mesh tier is selected from projected quad size. It is
screen-size driven rather than a fixed-distance switch, so resolution and field of view retain the
same meaning. The owner could trade sharp peaks for broad terrain instead of merely changing color
or haze.

Frustum culling was already active. The observed cost was therefore not explained by drawing tiles
behind the camera; the remaining targets were triangle count, fragment work, regeneration and
publication.

### 4. Lightweight geometry and off-thread regeneration (0.4.93)

0.4.93 made the broad horizon deliberately coarse: each 512-block tile now carries 16-, 8- and
4-quad meshes (32-, 64- and 128-block quads), 672 base triangles across all three stored meshes,
instead of the previous 64/16/8 set. Only one tier draws. Back-face culling is enabled, frustum
culling remains, and exact cached coverage is now a 16×16 mask of 32-block chunk footprints.

Network payload decoding and mesh construction run on workers, both capped at two jobs. The owner
thread publishes at most four decoded results and snapshots one mesh job per pump; coverage scans,
pruning, dirty work, remesh scheduling, uploads and byte/time budgets are all bounded. Reach changes
retain useful tiles and meshes, send a rate-limited same-session demand update, and prune gradually
instead of clearing and rebuilding the disc.

A saved **Hill softness** dial now controls up to 32 cells (128 blocks) of worker-only feathering.
Changes debounce for 250 ms and enqueue four tile remeshes per pump rather than rebuilding the disc
at once. This directly addresses the owner's severe reach/shape-change hitching report.

The owner reported that the large geometry reduction was successful and produced no visual
difference. No controlled final FPS or hitch trace was taken, so “lightweight” is established for
geometry and bounded scheduling, while final frame-time impact remains open.

### 5. Buried overlap join (0.4.94)

Exact-height contact improved the original gap but left small edge slits in some places and visible
Farseer overlap in others. Simply lowering the height field had previously reopened a sky slit,
because a height field has no vertical face between the two heights.

0.4.94 therefore treats the two symptoms as one ownership-boundary problem:

- the ordinary Farseer surface at a live cached edge sits 32 blocks below the real height;
- a lightweight skirt bridges from that buried surface to the exact cached height;
- the coverage shader leaves a 4-block buried underlap inside cached coverage, where normal depth
  ownership keeps real terrain in front and the Farseer surface fills only missing pixels;
- anchored join tiles use the 16-quad/32-block tier so every exact chunk boundary exists. The broad
  horizon retains its much cheaper tier selection.

A straight 512-block join adds 32 skirt triangles. Top vertices are shared, and skirts exist only
on cached/uncovered boundary segments. This is intended to remove both holes and coplanar overlap
without returning the detail that the owner had already found unnecessary.

### 6. Verification and artifacts

- Final Release build: zero warnings and zero errors.
- Final fast checks: **25,115 assertions, zero failures**.
- `dist/vintagehorizons_0.4.94.zip`: 652,086 bytes, 41 readable entries, manifest 0.4.94, one DLL
  byte-identical to the Release DLL, no PDB; SHA-256
  `718D8EFD656CE1AB186A374539A52CA426AD637C8E33DF48ACD581C65A0B56C4`.
- The archive was copy-installed beside older rollback builds with the same hash.
- Exact final source/test/version checkpoint: `0df61f4`.
- Documentation validation: **10,661 checks, zero failures**.

The 0.4.94 GLSL is source/static-checked but has not yet been compiled by a game run. Its final join
is not human-tested. No final FPS, GPU-time or hitch measurement is claimed.

---

## Delivered

- **0.4.88 (`c66e0f6`):** lazy client tile store, bounded nearest-first reads, exact authoritative
  replacement, current-session network authority and off/world cleanup. SHA-256
  `09113D4480428143F7D3547F4B5C5A149E404347958B6FCECF0248FFC38BD422`.
- **0.4.89 (`bf2b88f`):** persist authoritative replies that arrive before the client store path.
  SHA-256 `9852EF780B835EAAD3774C91F70E06E3601AF6323D1A4AB1692211E45DA3A4D8`.
- **0.4.90 (`c49edae`):** Farseer tab naming, expanded/standardized threshold scale, two-line distance
  labels, cached-edge anchoring and first join smoothing. SHA-256
  `3F888EF0D5819650B4FC8D917BEE1390F572D3E69E079BC7D618FF60C98FFC94`.
- **0.4.91 (`37d3c1d`):** repaired live reach application and stronger height-field feathering.
  SHA-256 `F5074D3820D1AA661A04CDDF296C1950A6128F6DE443FD7A6F04ABB0088E2536`.
- **0.4.92 (`c7924c0`):** saved, screen-size-driven outline-detail dial. SHA-256
  `EA167D99A7F586239CFF8C2EBD9B2DF2CE8C90190F569E6F09D8BF93759082D9`.
- **0.4.93 (`e2f6513`):** 16/8/4 meshes, 32-block exact coverage, back-face culling, bounded
  decode/mesh/publication/remesh work, retained reach changes, and the effective Hill softness
  dial. SHA-256 `4FEC8CFE718432810B1D1D684615E1B5E74FB185785459B0C60CAAF88B7E3AC5`.
- **0.4.94 (`0df61f4`):** 32-block-deep cached join, lightweight skirts and a 4-block buried
  underlap. SHA-256 `718D8EFD656CE1AB186A374539A52CA426AD637C8E33DF48ACD581C65A0B56C4`.

## Decisions

- **Farseer is the player-facing feature name.** It credits the original mod; the remaining rename
  and complete MIT-license bundling are follow-up work.
- **Extremely simple geometry is intentional.** The owner saw no visual loss from the 0.4.93
  reduction. Representative rolling shape outranks terrain detail.
- **Regeneration must be incremental and worker-owned.** Reach changes retain existing work;
  shape changes debounce and schedule bounded remeshes.
- **The join is an underlap, not exact coplanar contact.** Real terrain owns depth; a buried Farseer
  strip and minimal skirt fill numerical/raster holes.
- **Only join tiles pay for exact 32-block geometry.** Broad-horizon tiles remain free to use the
  cheapest projected tier.

## Traps

- Durable forms are promoted as G209-G211.
- **Trigger:** lower a surface-only join. **Failure:** the height difference becomes a visible sky
  slit. **Safer:** add an actual skirt/curtain between the buried field and exact live edge.
- **Trigger:** clear all client state when reach changes. **Failure:** the whole disc decodes,
  remeshes and uploads again, producing severe hitches. **Safer:** retain the intersection, update
  demand in place and prune/schedule through bounded queues.
- **Trigger:** judge Farseer cost only by frustum culling. **Failure:** visible tiles can still carry
  excessive triangles, fragments and regeneration work. **Safer:** price geometry, worker work,
  owner-thread publication and GPU shading separately.
- **Trigger:** receive network tiles before `LevelFinalize`. **Failure:** the first upgraded session
  displays them but does not persist them because the store path is not open yet. **Safer:** retain
  authoritative confirmations and backfill them when the store opens.

## Flagged and unverified

Judgement calls awaiting human review:

- Whether 0.4.94 removes both the remaining sky slit and visible overlap from the same screenshot
  locations and oblique approach angles.
- Final preferred values for Outline detail and Hill softness.
- Whether island/inlet refinement needs more work after the owner is satisfied with the join.

Claims lacking required evidence:

- 0.4.94's shader has not compiled in game and its join has not been seen.
- Final FPS/GPU cost and reach/softness-change hitching have no controlled before/after capture.
- Persistence is human-seen across leave/rejoin, but cold client restart, full-disc read-back,
  dedicated/multiplayer fairness and mixed-version transport remain open.
- Most internal/config/command “Silhouette” names and full bundled Farseer MIT-license credit remain
  to be changed deliberately; protocol names must not be renamed casually.
- Terrain Ahead (P6) and canopy (P7) have not begun.
