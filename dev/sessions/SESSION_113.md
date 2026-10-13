# Session 113 — Doden's Landscapes: the rename, the licence, and three long-standing faults

**Date:** `2026-10-07..13`
**Branch/commit:** `codex/water-depth-light`; checkpoints 0.4.136 `49df945`, 0.4.137 `ed1faa3`,
0.4.138 `346265a`, 0.4.139 `d9c99a9`, 0.4.140 `bbed35b`, 0.4.141 `8f66b6a`, 0.4.142 `f7cc8cd`;
nothing pushed
**Mod version:** `0.4.136` - `0.4.142`
**Assist protocol / blob / schema:** `1 / 4 / 6` (unchanged numbers; the network channel name changed
in 0.4.138, see `dev/WIRE_HISTORY.md`)

> Session records are Tier 3 history. Write narrative as needed, but preserve the four required tail sections so future harvesting remains mechanical.

## Context and investigation

The owner tested 0.4.135 on a fresh world and accepted it, then moved toward a public release: a new
name, the licence, the mod's behaviour beside other LOD mods, and a clean log. Three faults surfaced
on the way, each from his own sessions: a silhouette "stopped answering" warning, red shader errors
in every startup log, and "?" textures on near cached walls. Out of scope: the held
air-above-owned-ground change (Session 112), and pushing or publishing anything.

## Work narrative

### 1. 0.4.135 accepted; recorder removed (0.4.136)

His 0.4.135 log on a fresh world: half of freshly announced chunks handed over within 94-219 ms and
95% within 281-578 ms after the first minute; every stand-in drew only its waiting quarters. One
outlier: an 18.4 s level-1 stand-in at the player whose reason was "had not been asked for yet";
unexplained, recorded as a watch item only. He accepted the fix; 0.4.136 deleted
`LodTerrainRenderer.StandInRecorder.cs` and its three call sites and kept `.vhstandin` (now
`.dlstandin`) as a session switch.

### 2. The silhouette stall warning was a lost preview (0.4.137)

His session showed "the world generator stopped answering horizon silhouette previews" at 12:36:33.
The logs said otherwise: suspends kept succeeding (autosave at 12:36:55 and three pauses), the chunk
thread made about 260 idle passes a second, generation continued, and no server error was logged.
The decompiled 1.22.7 engine explains it (G273): pausing takes `PauseAllWorldgenThreads`, and on
resume `ServerThread.Process` runs `Update()` before `UpdatePausedStatus(false)` releases the hold, so
a peek dequeued in that first tick fails the pause's CompareExchange and is discarded with no
callback. A pause at 12:36:08 and a resume at 12:36:13 bracketed the lost preview; this instance is
timing-consistent, not proven. 0.4.137 gives the stall watch the chunk thread's pass count
(`ChunkThreadRest`): silence while at least 100 passes were made is `Lost`, and every outstanding
preview is re-issued once; a preview lost twice is given up. Silence with the thread standing still is
still the 0.4.77 stall. Checks cover alive, frozen, creeping (few passes) and answered-then-frozen.

### 3. The rename to Doden's Landscapes (0.4.138)

The owner chose the name ("Vistas" is taken by a Vintage Horizons derivative; "Far-" names collide
with Farseer), the mod id `dodenslandscapes`, `[Landscapes]` for chat and log, `.dl` commands, a move
(not a copy) of 7.8 GB of caches, and credit to AliasFactory. A scripted rename changed 114 files:
464 prefixes, 179 lower-case ids, 554 command mentions, 62 display-name strings, with protected
strings for formats that must keep their old spelling - the cache's DB type code, the height-sample
headers, and the game start-up file's `VintageHorizons.AllCoresCollector` marker and
`.vintagehorizons-backup` suffix. The assets folder moved to `assets/dodenslandscapes`, and the code
file became `DodensLandscapes.dll`, because the engine loads mod assemblies by `LoadFrom` and two mods
sharing a simple name risk sharing one assembly (G276). `ModIdentity.TakeOverLegacyData` moves
`ModData/vintagehorizons` and both ModConfig files once per process from every system's `StartPre`,
never over a newer file. C# namespaces and project names were left as they are. Played on
2026-10-13: the server log shows the folder and both files moved at first start, and the world drew
1,148 sections from the moved cache. A one-character trap during the rename work - PowerShell's `rd`
alias outranks a function of the same name, so a helper named `rd` tried to delete the live log and
only the game's file lock stopped it - is a tooling lesson, not a repository gotcha.

### 4. Other LOD mods: always draw, notify (0.4.139)

0.4.138 also made every system stand down while the original Vintage Horizons was on. The owner
rejected stepping aside for any LOD mod ("Ours is the best and should rise to the top"; players
manage their own mod list). Detection options were discussed in turn: a fixed list (upkeep), an IL
scan of every installed mod for `Camera.ZFar` writes (Farseer, Distant Vistas and this mod all set
`MainCamera.ZFar`; a read-only probe of his Mods folder was started and abandoned), and finally the
existing list kept and extended. 0.4.139 removes `deferringTo`, `IgnoreOtherLodMods` and `.dldefer`;
remaining guards check only whether the renderer exists. `OtherLodMods.Inspect` names every drawing
mod; Distant Vistas (1.1.3, a Vintage Horizons derivative that draws far LOD) and the original
Vintage Horizons join the list. `LodModNotice` opens at every login: a named conflict offers only OK;
otherwise a general reminder offers "Don't show again" (`HideLodModNotice`), which never hides a
named conflict. The 0.4.138 stand-down was undone; the legacy data move is skipped while the original
is enabled. The game-launching matrix's three deferral scenarios became `lod-conflict` and
`farseer-off` (not run). He saw the general notice and chose "Don't show again".

### 5. Licence (in 0.4.140)

The licence was plain MIT, (c) AliasFactory; a "conduct condition" existed for a few hours on
2026-07-25 and only a stale csproj comment remained of it. The owner chose GPL-3.0 only, copyright
"Doden", with a section 7 additional permission for linking with Vintage Story and its libraries
(FSF wording). `LICENSE` is the official text from gnu.org (35,149 bytes, SHA-256
`3972dc97...36986`); `NOTICE` holds the copyright, "version 3 only" and the exception;
`THIRD-PARTY-NOTICES` reproduces AliasFactory's and Badgerson's MIT notices verbatim and lists what
was adapted from Farseer (renderer far-plane setup, the game's haze and sky fade on distant terrain,
the silhouette shaders), stating that the seam fog is this project's own. TopoHorizon is not listed:
the owner vouches only for this project's link to AliasFactory, whose own credits stay in its code.
All three ship in the zip; a check pins them.

### 6. All checks green

`GameCollectorChecks` drove the editor against his installed start-up file, which the all-cores
switch had edited, so 14 of 21 assertions failed from 2026-09-29. It now takes the first copy still
as shipped: the installed file, else the switch's backup, else a built-in sample. 26,208 assertions,
0 failures, on his machine with the collector on.

### 7. The startup shader errors (0.4.140)

Six passes (lodterrain, lodterrainindirect, lodterrainpacked, lodwaterdepth, lodwater, lodwateroit)
logged "Missing entry point" link errors in every archived session back to 0.4.134, twice. The first
round: the renderer's constructor called `LoadShader()` inside the engine's `StartModsFully`, which
`HandleServerReady` follows at once with `ReloadShaders`; `RegisterFileShaderProgram` reads the files
immediately, against an include table that only a full load refills, so `lodterrainbody`/
`lodwaterbody` were "not found" (G274). The second round is not a failure: it is the engine's
"(x_x) Captured N issues during startup" summary replaying startup warnings and errors. 0.4.140 drops
the constructor's build (the `ReloadShader` event is the first), adds `EnsureShadersLoaded` in
`PrepareBeforeFrame` as a safety net, and removes the "provisional first attempt" leniency so every
failure reports at full volume. The engine suppresses reloads only during a graphics-preset change.
Confirmed in his 0.4.140 and 0.4.141 logs: "shaders ready after 1 attempt(s)", no link errors.

### 8. The "?" walls (0.4.141-0.4.142)

His screenshot showed the game's "?" image on the walls of near cached rock and soil; tops were
right; he had seen it for a long time. Three routes were ruled out in the decompiled engine and his
log: a missing image file (the engine would log "Texture asset not found"), an unbaked name left at
sub-id 0 (`compose` registers every named texture), and an empty rectangle sampling the atlas origin
(the shader skips it). Rather than theorise further, 0.4.141 refuses any face texture picked by name
whose content is the "?" image (the unknown slot, or an exact match of its average colour) and logged
each block. His next session named the route (G275): ordinary cube blocks - granite, basalt,
peridotite, phyllite, ores, crackedrock, soil, water - carry `all=game:unknown` at slot 0, and the
walls' skip-the-top rule (added for redwood quarter-trunks) skipped every face on a block whose faces
share one texture and landed on "all". The refused wall copies the top, which for these blocks is
their side texture; he played near the cliffs and it looked good. 0.4.142 reduces the twelve log
lines to one plus a count in the texture status line.

---

## Delivered

- **0.4.136** (`49df945`): temporary stand-in recorder removed. Owner-accepted fix of 0.4.135.
- **0.4.137** (`ed1faa3`): stall watch tells a lost preview from a stopped generator by the chunk
  thread's pass count; lost previews re-issued once. Not played specifically.
- **0.4.138** (`346265a`): renamed Doden's Landscapes; `.dl` commands; `[Landscapes]` prefix;
  `DodensLandscapes.dll`; legacy data moved once. Owner-played: move confirmed.
- **0.4.139** (`d9c99a9`): never steps aside; `LodModNotice`; list extended; stand-down undone.
  Owner-played: general notice seen and dismissed.
- **0.4.140** (`bbed35b`): shader startup fixed; GPL-3 licence files shipped; collector checks pass.
  Owner-played: confirmed in the log.
- **0.4.141** (`8f66b6a`): "?" walls fixed. Owner-played: confirmed visually and in the log.
- **0.4.142** (`f7cc8cd`): one log line for "?" faces.
- **Documentation:** this record; CHANGELOG 0.4.136-0.4.142 and Unreleased; G273-G276;
  WIRE_HISTORY channel rename; TODO/DONE; STATUS; README and CLAUDE.md for the new name and commands.
- **Tests:** 26,214 assertions, 0 failures (from 26,156 with 14 environmental failures).

## Decisions

- **Name:** Doden's Landscapes, id `dodenslandscapes`; `[Landscapes]`; `.dl` commands; C# namespaces
  unchanged. Rejected: "Vistas" (taken), "Far-" names (Farseer).
- **Legacy data:** moved, not copied (owner); never over a newer file; not while the old id is
  enabled. Rejected: copy (7.8 GB extra), fresh start.
- **Other LOD mods:** always draw, no step-aside switch (owner: "We don't need switches to confuse
  situations"); a login notice, named conflicts unhideable. Rejected: stepping aside, a per-conflict
  "Don't show again", an IL scan of every installed mod ("I don't like the idea of having to analyze
  every mod a player has").
- **Licence:** GPL-3.0 only, (c) Doden, Vintage Story exception; MIT notices for AliasFactory and
  Farseer only. Rejected: "or later"; a TopoHorizon notice (not this project's link).
- **Lost previews:** re-issue once when the chunk thread is alive; the engine's own drop is not
  patched.
- **Shaders:** build only on the engine's reload; no provisional attempt.
- **"?" faces:** refuse by content, fall back to the top; the block-colour path still accepts the
  image, so blocks the game itself draws as "?" keep that look.

## Traps

- **Trigger:** a peek queued across a singleplayer pause. **Failure:** the engine discards it with no
  callback. **Safer:** judge silence against the chunk thread's progress; re-issue (G273).
- **Trigger:** building mod shaders from a ModSystem start. **Failure:** includes not found, link
  errors, replayed by the engine's startup summary. **Safer:** build on `ReloadShader` (G274).
- **Trigger:** picking a texture by face name with a skip rule. **Failure:** cube blocks fall through
  to `all=game:unknown`. **Safer:** refuse the "?" image by content (G275).
- **Trigger:** renaming a fork. **Failure:** two mods with one assembly simple name can share code.
  **Safer:** rename the assembly with the mod id (G276).
- **Trigger:** a Python text round-trip on Windows. **Failure:** `read_text` folds CRLF; CRLF scripts
  and `.cmd` launchers change silently. **Safer:** read and write bytes, and assert on the
  line-ending style before editing (`check-matrix.sh` is CRLF).

## Flagged and unverified

- **Judgement calls awaiting review:** the plural wording of the conflict notice ("several times
  over") was adapted from his single-mod text; `.dlstandin` kept as a session switch.
- **Not established:** the lost preview at 12:36 is timing-consistent with G273, not proven; 0.4.137
  has not been seen to re-issue a preview in play. Running beside the original Vintage Horizons is
  untested. The notice's layout at other GUI scales is unseen. The 18.4 s stand-in outlier is
  unexplained. Whole-game memory reached 12.1 GB in his 0.4.139 session while flying up to 5,200
  blocks a minute, the highest seen; not investigated.
