# Session 120 — 0.5.2-dev.3: the first ModDB reports, an Algernon crash, a stalled generator, shorter logs, reeds

**Date:** `2026-10-24..31`
**Branch/commit:** `main`; dev builds `0fc55ef` (0.5.2-dev.1), `5db022b` (0.5.2-dev.2), `17aca13` (0.5.2-dev.3), none pushed
**Mod version:** `0.5.2-dev.3` (a test build; dev.2 had one session on 1.22.7, dev.3 is unplayed)
**Assist protocol / blob / schema:** `1 / 4 / 6` (unchanged)

> Session records are Tier 3 history. Write narrative as needed, but preserve the four required tail sections so future harvesting remains mechanical.

## Context and investigation

0.5.1 reached ModDB (`mods.vintagestory.at/show/mod/75034`; the slug address 404s) and the first player
reports came in: `dev/PLAYER_REPORTS.md` (git-ignored at the owner's request) holds each with its
evidence. The owner asked to fix everything reported on the mod page, dropped the Sheydermod items, then
added the easy TODO items and the first pull request on GitHub.

Evidence used: Akekho's logs (linked from a comment), decompiled 1.22.7 and 1.22.0 engine source, decompiled
Watersheds 7.0.4, Algernon's Terrain Sampler 1.3.0 and Optimum's runtime donors, the owner's own 1.22.0
and 1.22.7 session logs, and isolated headless servers on throwaway worlds. The pull request's code was
read but never run.

## Work narrative

### 1. 1.22.0 support (dev.1)

Requests came for older games (1.22.3 named). `modinfo.json` now declares game 1.22.0. The owner played
0.5.2-dev.1 on a separate 1.22.0 install on 2026-10-28, two sessions, no errors or warnings from the mod;
his reading of the second session also showed the save reopening after a clean quit.

He later settled the rest: compile against 1.22.7 and keep declaring 1.22.0 (2026-10-30), and do no
further testing below 1.22.5; the release text will say the mod was briefly tested on 1.22.0 but is
intended for 1.22.5+ (2026-10-31).

A build compiled on 1.22.7 asks for a newer library than 1.22.0 has: 1.22.0's `VintagestoryAPI` is stamped
assembly version 1.0.0.0 and 1.22.7's 1.22.7.0. It loads because the game's start-up handler
(`Vintagestory.Common.AssemblyResolver.AssemblyResolve`) drops the version from the name. A throwaway probe
loaded the DLL through that handler and ran `RuntimeHelpers.PrepareMethod` on all 7,617 methods of 609
types, 0 failures on both games; a negative control proved it throws `MissingMethodException` for a
removed method. It does not cover Harmony's attach step. G308.

### 2. Akekho's crash: the sampler races the game's map layers (dev.2)

The server died in `chunkdbthread` with `Index was outside the bounds of the array` in
`NoiseLandforms.GetParentLandformIndexAt`. That method writes each variant's weight into a shared static
list, sums them, then walks the list subtracting; a second thread rewriting the weights between the two
loops leaves the walk without an answer. The chunk thread was building a map region for a silhouette
preview; our background thread was running Algernon's sampler, which runs the same layers. Its
`lock (mapGen)` only keeps its own callers apart, because the engine never takes that lock. Every map
layer also keeps its position seed on one shared object, so an overlap that does not crash can silently
pick a wrong landform. Watersheds replaces these methods for the whole game, so Watersheds users are safe;
Optimum was decompiled and is not involved.

`EngineMapGate` puts one lock around `GenMaps.OnMapRegionGen` and around each Algernon call, point by point,
only when Algernon runs without Watersheds. It replaces no game code and changes no result. If the game's
generator is not the one this build knows, the sampler is not used and silhouettes build from previews.
Offline, the game's real picker failed tens of thousands of times in 2 million calls unlocked (36,291-54,895
across runs) and 0 locked.
G306, decision 65.

### 3. Proving the lock on a real server (dev.2)

The owner asked for the proof, so a throwaway mod (`racetest`, session scratchpad only) ran through
`scripts/silhouette-compare.ps1` with `-ModSource`, `-ModConfigSource`, `-RequiredModIds` and
`-NoPresample`: previews at far random chunks on the main thread (so new map regions keep being built),
Algernon sampling on a background thread, marker files in `ModConfig` choosing the mode, and each tick gap
over 500 ms logged with its GC pause, collection counts, `Monitor.LockContentionCount` change and heap. All
runs were Algernon 1.3.0 without Watersheds, range 16, headless, throwaway worlds.

| Run | Crash | Samples | Previews | Slow ticks |
|---|---|---:|---:|---|
| Lock bypassed, 1.22.7 | Within a second, Akekho's exact stack | - | - | - |
| Locked, random positions, 1.22.7, 16 min | None | 654,765 | 5,371 | 64 |
| Locked, random positions, real 1.22.0 server, 1.22.7-compiled zip, about 13 min | None | 597,696 | 4,661 | 66 |
| No sampling (control), pregen 2:04 | None | 0 | 1,671 (11.4/s) | 0 |
| Real builder's tile pattern, pregen 2:04 | None | 20,758,528 | 1,918 (11.4/s) | 0 |
| Locked, random positions, with measurements | None | - | - | 58 stalls, 115,342 ms |

The slow ticks looked like the lock and were not. In the measured run, GC paused the game for 112,745 ms of
the 115,342 (98%), one gen2 per stall, at most 3 lock waits per stall, and the heap reached 9,768 MB. Random
positions defeat Algernon's region cache (512 entries, 30-minute sliding expiry), so every sample built a
region. The real builder works one 512-block tile at a time, and that pattern matched the control exactly.
G309.

### 4. "World is in use" is the game's, not ours

Source-traced: `ServerThread.Process`'s crash branch never runs `ShutDown`, so
`ServerSystemLoadAndSaveGame.OnSeperateThreadShutDown`, the only `gameDatabase.Dispose`, never runs after a
chunk-thread crash, and the save stays open until the process exits. Nothing was built for it and it was
not run. G307.

Our savegame reader did have a real close race: `Dispose` could close the connection under a running
statement. Reads now hold the reader's gate for the whole statement, and closing waits at most for the row
in hand. It left no handle behind, so it was not the cause of the report.

### 5. crystalkiori's stalled generator: G238's unfinished half (dev.2)

`/dlgen` and `/dlworldgen` only answered "waiting for an old distant-terrain save". Session 104 had proven
the dirty age is not a save latency and relabelled the log line, but left the gate from 0.4.72 reading it.
A section that keeps changing kept its first-dirty clock while its saves succeeded, so the gate could close
for good. `LodSaveDirtySet.Saved` now restarts a key's clock when a write of an older revision lands, so an
age means time since the key last reached the disk. A failed write keeps its age, so the gate still brakes
on a stuck disk. G238 revised.

### 6. Shorter logs (dev.2)

Akekho's 8.5-minute session put 47% of the client main log and 28% of the server's in our lines. Routine
detail now goes through `LodDetailLog.Detail`, 72 calls, at the logger's debug level, which the game writes
to `client-debug.log` and `server-debug.log` beside the main logs. Warnings, errors, the lines that say what
a session is running and one hitch summary a minute stay in the main log. `VINTAGEHORIZONS_STATS=1` keeps
everything in the main log, because the bench and check scripts read it there. Measured on two archived
sessions: about 150 of our lines down to 17 (client), 83 down to 10 (server). The owner's 2026-10-30 session
on dev.2 agreed: 17 of the main log's 365 lines, and 117 of ours in the debug log. Decision 66, G310.

### 7. Small items (dev.2)

- **Cached distance minimum 512** (davidkiraly, from the live page). Farseer's reach stays 1,024.
  `LodThresholdScaleLayout` holds the draw distance's own limits instead of borrowing Farseer's clamp.
- **Farseer's held ticks split by reason:** `EnumSilhouetteYield` (generation job, engine chunks, server
  load, cache backlog). This was the TODO item that suspected `SaveBacklogAge`.
- **`BuildPath`'s centres list reused**, for the fog outline's main-thread allocation.
- **`SteadyStateAllocation` warmed up properly:** its measured loop called two methods the warm-up did not.
  It failed 3 of 7 runs in Session 108; it has passed in every run since the change, at least nine. Nothing
  proves those two methods were the cause.

### 8. Pull request 1: the game's minbrightness (dev.2)

ArtificialSloth's pull request made distant terrain follow the game's `minbrightness`, adding grey to the
final light. The game floors `bBlock` before mixing it with the sun (`fogandlight.vsh` line 130, in the
`DYNLIGHTS > 0` branch only), which with no block light scales the sun light and keeps its hue. The owner
chose to rewrite and credit. `LodTerrainRenderer.BrightnessFloorScale` works the factor out on the CPU and
feeds the uniform `brightnessFloorScale`: exactly 1 at the default of 0, and 1 when dynamic lights are off.
`StaticAssetChecks.BrightnessFloor` compares it with a transcription of `applyLight`. Farseer silhouettes do
not take the floor. The commit carries a `Co-authored-by` trailer; the owner posts the note on the pull
request himself. G311.

### 9. Not done

Sheydermod's crash (the engine's fixed 100-slot shader table) was dropped by the owner; the diagnosis stays
in `dev/PLAYER_REPORTS.md`. NateDoesLife's far block textures need the owner's design go-ahead.

### 10. Reeds at distance (dev.3)

The TODO's clue, runs "with no leaf above", meant no leaf directly above. A cache scan read what is over each
reed run in the same column: on `6bab2982` 9,277 reed runs, 13 topping their column, the rest under leaves or
logs, and 8,130 of the 9,255 with a run above had a gap of air first. `reedpapyrus.json` sets
`rainPermeable` on `*-land-*`, the game leaves such blocks out of `RainHeightMap`, and `LodWorker.Capture` starts
there. A higher leaf lifted the start, which is the whole "under leaves" rule. Water-habitat reeds are not
permeable, but `FluidOrSolid` answers the water in their cell: no run of one exists in either cache read. G312.

Tall grass has the same cause (486 tops against 497,155 under something), and capturing it would raise every
meadow by a block, so `LodBlockPolicy.ReedTable` covers `BlockReeds` only. The owner then asked whether a cheap
model could beat a cube. Reeds stand about three blocks tall, the game's far model is two crossed planes, and
packed quads had two free face codes. He asked for performance cost per option. All were estimated under 0.5% of
his quads, because only about 620 blocks of radius draw them, so he chose on looks: crossed planes first, a single
spike being "lackluster", and grass probably never.

`FlagCrossed` makes a run skip the cube path. Level 0 emits one `DiagonalRising` and one `DiagonalFalling` quad;
coarser levels emit an opaque mat a quarter block over the ground. The vertex stage swaps a plane's ends when the
camera is behind it, so one quad serves both sides, narrows the top by half, and darkens the foot. The planes
carry no texture slot, so the band leaves them alone. They sink half a block so a slab leaves no gap, and a reed
hides nothing: `IsThinRun` includes it. A reed in water could not replace the water, which would cut a hole in the
surface, so capture puts it in the air above and the mesher takes a block off its height. Under-tree reeds change
shape as soon as a cache loads, because palette flags come from the live block.

---

## Delivered

- **0.5.2-dev.1** (`0fc55ef`): game 1.22.0 declared, compiled on 1.22.0.
- **0.5.2-dev.2** (`5db022b`): everything above, compiled on 1.22.7, 862,211 bytes, copy-installed to both
  Mods folders, not pushed.
- **0.5.2-dev.3** (`17aca13`): reeds captured above the rain map and in water, drawn as crossed planes; copy-installed to
  both Mods folders, not pushed.
- Checks: 26,569 assertions, 0 failures on 1.22.0 and 1.22.7 at dev.2, three identical runs at that count and
  six at 26,558; 26,646 at dev.3, five identical runs. New checks cover the map gate, `Saved`, the reader's close,
  the brightness floor, the log split, reed capture and crossed planes.
- Documentation (2026-10-31, at the owner's request): this record, CHANGELOG, GOTCHAS G306-G312 and G238,
  ARCHITECTURE decisions 65-67, TODO, STATUS, README, RELEASING and the private working rules.

## Decisions

- **Game versions:** declare 1.22.0, compile on 1.22.7, intended for 1.22.5+. No further testing below
  1.22.5; the release text says so. Rejected: compiling on 1.22.0 (it would not see newer API).
- **Map layers:** one lock around both sides, Algernon without Watersheds only. Rejected: replacing the
  game's methods as Watersheds does (touches world generation for every mod); dropping Algernon when alone
  (it is the supported sampler).
- **Dirty age:** restart the clock on a landed write and keep the gate. Rejected: removing the gate, which
  still has a job on a stuck disk.
- **Logs:** nothing deleted; the detail moves to the debug logs. Rejected: a Developer switch, which every
  install would have to remember.
- **Pull request:** rewrite with the game's formula and credit the author. Rejected: merging as written.
- **Reeds:** two crossed untextured planes, all four reed types, land and water. Rejected: a single spike, the
  real texture, a forced re-sweep of existing worlds. Grass is out of scope (owner: probably never).
- **Dropped by the owner:** the Sheydermod items.

## Traps

- The game's map layers are not safe on a second thread, and a mod's own lock does not help (G306).
- A chunk-thread crash leaves the game holding the save open until the process exits (G307).
- A load probe that uses only `ModAssemblyLoader`'s handler fails falsely, and a shared `obj` folder quietly
  builds the "1.22.7" host against the old API (G308).
- A sampler stress test at random positions measures garbage collection, not locks (G309).
- Scripts that read the main log now need `VINTAGEHORIZONS_STATS=1` (G310).
- `#define MINBRIGHT` reaches vertex shaders only, and the floor applies only with dynamic lights on (G311).
- Capture misses whatever rain falls through, and reads water in a cell it shares with a reed (G312).
- From the session itself: `-RequiredModIds a,b` through `pwsh -File` arrives as one string, so call the
  script with `-Command` and `@(...)`; a Bash heredoc holding an apostrophe can fail, so write the file with
  the Write tool; `scripts/silhouette-compare.ps1` refuses to run while any game process is open.

## Flagged and unverified

- **Judgement calls for the owner:** the disclaimer's wording at release; Nate's far textures; whether to
  tell Anego about G307 and the map layers, and Algernon about the race.
- **Harness-tested and headless only:** the lock with the real silhouette builder and a connected player on
  an Algernon-only world. The tile test called the same sampling code with the builder's pattern, not the
  builder itself. Akekho's own setup is the final check.
- **Source-traced, not run:** G307.
- **Not played:** the brightness floor at a raised setting, and the shader change itself: there is no GLSL
  compiler here. The owner's 2026-10-30 session ran dev.2, but its log was not checked for shader errors.
- **Unconfirmed by the players:** crystalkiori's and Akekho's fixes; crystalkiori's log was never seen, so a
  failing disk was not ruled out for that report.
- **Not proven:** that the warm-up was the flaky check's cause. The fast tier's count held still in nine runs,
  then read 26,548 and 26,598 on one binary before five runs at 26,646, so it still varies and the suite is unknown.
- **Closed by the owner, so never checked:** client-side patches on 1.22.0 after dev.1, and 1.22.1-1.22.4.
- **Reeds, not seen in game:** the look, and the shader, which has not been compiled. Their number is an estimate
  (2,000-15,000 in range, scaled from reeds under trees), and so is the frame cost.
