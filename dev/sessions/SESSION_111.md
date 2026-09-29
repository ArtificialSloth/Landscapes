# Session 111 — The exploring stutter solved: the chunk thread rests, and all-cores collection

**Date:** `2026-09-28..29`
**Branch/commit:** `codex/water-depth-light`; `92e47ac` (0.4.132), `4b29dfa` (gate tool), `f2e47e9` (0.4.133), `7abcb14` (launcher), `ad6b02b` (0.4.134)
**Mod version:** `0.4.132` through `0.4.134`
**Assist protocol / blob / schema:** `1 / 4 / 6`

> Session records are Tier 3 history. Write narrative as needed, but preserve the four required tail sections so future harvesting remains mechanical.

## Context and investigation

The owner asked to continue from Session 110, whose open stutter items were: confirm whose closure
made 60-84% of the garbage on the game's chunk thread while exploring new terrain, try a larger
gen0 budget as a launch setting, move the recorder's minute report (14-58 ms on the render thread)
off-thread, and look at this mod's remaining garbage. By the end he reported the stutter gone.

## Work narrative

### 1. The closure is vanilla's, and the chunk thread busy-waits

Decompiled 1.22.7 IL: `ServerSystemSupplyChunks.loadOrGenerateChunkColumn_OnChunkThread` allocates
`<>c__DisplayClass17_0` at `IL_0000`, on every call. It captures `this` and `chunkRequest` for the
main-thread task on its `Done` branch, and a captured parameter makes the compiler build the closure
at method entry. The only other `<>c__DisplayClass17_0` in `VintagestoryLib` is
`ServerSystemEntitySimulation`'s (player respawn). None of the core mods' same-named closures is
worldgen code; of the owner's enabled mods only Watersheds has any, a database store far too rare
for the rate and a generic closure whose logged name would carry its type arguments.

Why so many: `ServerSystemSupplyChunks.GetUpdateInterval()` returns
`additionalWorldGenThreadsCount - 1`, which is -1 with the default `MaxWorldgenThreads: 1` whenever
column requests are queued on a machine with 8 or more logical processors, and
`ServerThread.Update` treats a negative interval as "go round again without sleeping". Generating new
terrain leaves neighbour columns parked in the queue at the pass their neighbours needed
(`ChunkServerThread.EnsureMinimumWorldgenPassAt` sets `untilPass`), so while a player explores, the
thread re-scans those parked requests back to back and allocates one closure per request per scan.
Over old terrain nothing parks, the queue empties, and the thread sleeps. The enumerator-to-closure
byte ratio in the owner's log implied about 140 requests per scan and about 130,000 scans a second
(G261).

### 2. Multi-thread generation checked first, at the owner's request

He asked whether multi-thread generation was on and whether turning it off fixed this before any
game code was patched. It was already off: his `servermagicnumbers.json` holds the default
`MaxWorldgenThreads: 1`, unchanged since April. The busy-wait exists precisely because it is off:
with a second generation thread the interval is 0 and the thread sleeps. Session 91's saved headless
runs (961 columns) show it: one thread, `chunkdbthread` about 0.94 of a core and the server 1.30
cores at 9.7 columns/s; two threads, 0.10 and 0.60 at 8.6/s; six, about 0 and 0.67 at 9.6/s. More
threads would stop the spin without code but were rejected in Session 91 because they change the
generated terrain. The owner chose a patch with a switch.

### 3. 0.4.132: the chunk thread rests, and the minute report leaves the render thread

`ChunkThreadRest` is a Harmony postfix on the scan, `tryLoadOrGenerateChunkColumnsInQueue`. After a
pass that found nothing, and only where the engine's own interval is negative (it was about to go
round again), the thread sleeps for `ServerThread.SleepMs`, the engine's own between-pass sleep (2 ms
while players are connected, the tick time on an empty server). A pass that found work never rests.
The hook is on the scan rather than the interval because the interval is a tiny virtual method the
JIT may inline into its caller before the patch lands, and the main thread reads it too (G262).
`/vhchunkrest on|off` switches it per session; `RestIdleChunkThread = false` in the server config
leaves it uninstalled. It counts passes, passes with work, idle passes, rests and the chunk
thread's exact allocation, reported in the recorder's server-work line (singleplayer), in the
server stats (`VINTAGEHORIZONS_STATS=1`) and as a session total at shutdown. A failure switches it
off with one log line.

The recorder's per-minute state moved into a window object: at the boundary the render thread takes
the closing readings and swaps in a fresh window, and the report is formatted and logged by a
background task, in order. Leaving the world, switching the recorder off and closing the game still
write in place. The game's logger locks and flushes the file on every line.

Checks: `ChunkThreadRestChecks` (the real method exists and patches by exact id; rests only after an
idle pass where the interval is negative; fails open) and an off-thread report check with a 50 ms
per-line logger. Three deliberate breaks were each caught.

### 4. Headless A/B of the rest

`scripts/worldgen-thread-gate.ps1` gained `-ChunkThreadRest On|Off|Absent` (Off installs the rest
and switches it off with `/vhchunkrest off` before pregen, so the off side still counts; Absent pins
it uninstalled). Vanilla plus this mod, Session 91's `p0-isolate-vh-r15` seed, `/wgen pregen 15`
(961 columns), one run each, the owner's game closed:

| Run | Pregen | Server | Chunk thread | Idle passes/s | Chunk-thread MB/s |
|---|---|---|---|---|---|
| Absent (vanilla) | 101.2 s (9.49/s) | 1.25 cores | 0.93 | not counted | not counted |
| Installed, off | 80.9 s (11.88/s) | 1.30 | 0.97 | 75,734-168,385 | 713-816 |
| On | 100.7 s (9.54/s) | 0.57 | 0.19 | 15-18 (rested) | 9-35 |

The installed-off run finishing 20% faster than both others is unexplained (section 11).

### 5. The owner's 0.4.132 play: less garbage, same feel

On 2026-09-28 21:40-21:48 he explored with the rest on, then switched it off at 21:46:31. With the
rest on, the chunk thread allocated 29-55 MB/s against 285 off; all allocation 339-644 against 803;
gen1 collections 179-326 a minute against 395; time frozen 2.7-5.2 s a minute against 6.2 s; 1% low
40-57 against 45 FPS. He said it did not feel any smoother. The felt freezes are the gen1
collections, 10-20 ms each, and there were still 3-5 a second; while they outnumber 1% of frames
(about 150 a minute) they set the 1% low whatever the total. They are driven by objects that live
briefly (the game's chunk meshes waiting to upload) surviving the small collections, which run after
every 48 MB (this CPU's default gen0 budget) at 7-18 a second. The closures died at once and only
made small collections more frequent (G263). The recorder's own report was no longer blamed for any
slow frame.

### 6. Optimize RAM is not a collector setting

He asked whether the game's "Optimize RAM" setting already dealt with this. It does not: nothing in
the game changes the collector's mode or budget. In the client (his setting is 1, Optimize) it sets
how long an untouched chunk stays unpacked (80 s off, 8 s Optimize, 4 s Aggressive) and the chunk
data pool size (5,000 / 2,000 / 1,500), and forces LOH-compacting full collections in the main menu
above 300 MB. Aggressive also forces one in game up to every ten minutes (30 s when unfocused),
halves particle pools and unloads sounds and shapes. The server compacts the LOH when its last
player leaves (G266).

### 7. The collector settings, probed offline

A .NET 10 probe on his PC (16 logical processors, 61.6 GB): `DOTNET_GCgen0size` is honoured but
capped at 128 MB (`GCGen0MaxBudget`; 256 MB requested gives 128). In a heap shaped like the game's
(a large long-lived graph re-pointed at new objects, brief-lived objects, throwaway garbage), 24 GB
of allocation gave: default, 516 collections, median pause 27 ms, longest 47; gen0 128 MB, 196
collections, median 64 ms, longest 75; Server GC adaptive, 54, median 22, longest 71; Server GC with
every heap fixed, 10, median 65, longest 246 (G265). 0.4.133 made the recorder log the collector in
force, and `scripts/vs-collector-test.cmd` (copied to his desktop) starts the game normally, with a
128 MB gen0 budget, or with the Server GC.

### 8. The Server GC: much smoother

His 0.4.133 session with the Server GC (2026-09-28 22:25-22:29, option 3): "Felt MUCH smoother."
After the first minute, collections fell from 450-750 a minute to 25-38, time frozen from 2.7-5.2 s
a minute to 0.26-0.40 s, the 1% low rose from 40-57 to 103-138 FPS and the 0.1% low from 26-39 to
45-59. Each collection averaged about 9 ms against 6. The first minute, while the world loaded,
had 56-68 ms collections (G267). The slowest frames are now 2-4 ms spikes in entity ticking and this
mod's `vh:prepare`. The recorder's "allocated in total" read 47-62 MB/s where its samples added up to
about 340: the approximate allocation count lags under the Server GC's large regions (G264).

### 9. 0.4.134: the opt-in setting, and memory in the recorder

The owner chose an opt-in mod setting and a report to the game's developers. A new **Performance**
tab in the settings editor holds **All-Cores Garbage Collection**, off by default. Save adds
`"System.GC.Server": true` and a marker property to the game's own `Vintagestory.runtimeconfig.json`
(named after the entry assembly, beside the game), keeps a one-time backup, writes through a
temporary file, and switching it off removes only what the mod added; a collector property set by
someone else is left alone either way. Each client start keeps the file in step with the setting (a
game update replaces the file) and logs what the session runs. A file that cannot be read or written
is reported and the setting is left as it was. The tab strip widened from 640 to 760 px for five
tabs. The recorder's collections line gained the longest freeze in one frame, the frames held 10 ms
or more, and memory (collected heap, committed, whole game), and window totals use the exact
allocation count. `GameCollectorChecks` drive the editor against a copy of the installed game's file;
two deliberate breaks were caught. 25,967 assertions.

### 10. The owner's 0.4.134 play: the setting works, and costs about 5% memory

2026-09-29 07:07-07:12: a short session with the setting off (the first minute stationary during the
startup sweep, then 533 blocks), then on through the Performance tab and a normal restart. The log
shows the Server GC running from the setting with no launch settings. Off: 129-148 frames a minute
held 10 ms or more by collections, 2.5-2.7 s frozen, longest 35-40 ms, 1% low 52-61 FPS, whole game
7.3-9.2 GB. On, after the first minute: 14-16 frames, 0.36-0.40 s, longest 22-23 ms, 1% low 92-97
FPS while moving 1,339-1,516 blocks a minute, whole game 9.3-9.5 GB and committed 7.6-7.7 GB against
7.3-7.6: about 0.3-0.5 GB, 5%. The first minute had 59-139 ms collections while loading. One 170 ms
frame spent outside the profiled frame (not a collection) is unexplained.

### 11. The bug report

A report for the game's developers was drafted for the owner to send: the busy-wait with its
measurements and two suggested fixes (sleep after a tick that made no progress, or allocate the
closure only on the `Done` branch), and the Server GC result with a suggestion to ship
`"System.GC.Server": true`, memory cost to be filled in from section 10.

---

## Delivered

- **0.4.132** (`92e47ac`): `ChunkThreadRest` with `/vhchunkrest` and `RestIdleChunkThread`; the
  recorder's minute report written on a background task; `ChunkThreadRestChecks`. **Tool**
  (`4b29dfa`): the worldgen gate's `-ChunkThreadRest` modes.
- **0.4.133** (`f2e47e9`): the recorder logs the collector in force. **Tool** (`7abcb14`):
  `scripts/vs-collector-test.cmd`.
- **0.4.134** (`ad6b02b`): the Performance tab and `GameCollectorSetting`; the recorder's longest
  freeze, held frames, memory and exact allocation; `GameCollectorChecks`.
- **Checks:** 25,967 assertions pass. All three builds verified, copy-installed and owner-played; no
  push. A bug report was drafted; the owner sends it.

## Decisions

- **Patch the game's chunk thread** (owner, 2026-09-28), after multi-thread generation was confirmed
  off; turning it on stops the spin but changes terrain (Session 91). The rest acts only where the
  engine would not sleep, and is on by default: it cuts total collection time about 40% under the
  default collector and frees a core, though it does not change the feel.
- **The all-cores collector is an opt-in setting that edits the game's start-up file** (owner,
  2026-09-28), off by default, plus a report to the developers. Documenting a launch setting only
  was the rejected alternative.
- **Not pursued:** a larger gen0 budget (capped at 128 MB; longer pauses in the probe) and the Server
  GC with a fixed heap count (a 246 ms pause in the probe).
- **The recorder writes its minute report in the background,** and in place only when leaving the
  world, switching off or closing.

## Traps

- **With one generation thread on 8+ logical processors the engine's chunk thread never sleeps
  while anything is queued.** G261.
- **Patch a method that cannot be inlined, not a tiny virtual getter.** G262.
- **Removing garbage that dies at once does not change the felt stutter.** G263.
- **`GC.GetTotalAllocatedBytes(false)` under-reads under the Server GC.** G264.
- **A gen0 budget is capped at 128 MB and a bigger one lengthens every pause; a fixed server heap
  count gave 246 ms.** G265.
- **"Optimize RAM" does not touch the collector.** G266.
- **The Server GC's first minute after joining has 56-139 ms collections.** G267.
- **An A/B whose off side uninstalls the instrumented code has no numbers for that side.** The gate's
  first "off" run pinned the rest uninstalled; the installed-but-switched-off mode was added.

## Flagged and unverified

- **The installed-off headless run was 20% faster** (80.9 s against 101.2 and 100.7). One run each;
  generation speed is shown equal only between rest on and the patch absent.
- **The Server GC's first-minute collections** (56-139 ms while the world loads) are not
  investigated.
- **Memory cost** comes from one short pair of sessions, the off one about two minutes with its first
  minute stationary: about 0.3-0.5 GB.
- **The Server GC is untested** with fewer cores, less RAM, on Linux or macOS, or where the game
  folder is not writable.
- **The rest is untested on a dedicated server with players**; headless and singleplayer only.
- **A 170 ms frame outside the profiled frame** in the 0.4.134 session is unexplained.
- **The next slowest frames,** entity ticking and this mod's `vh:prepare` (2-4 ms), are unexamined.
