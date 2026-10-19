# Doden's Landscapes

Distant Horizons-style extended render distance for [Vintage Story](https://www.vintagestory.at/).
It works on any server, and that server needs nothing installed. Founded on AliasFactory's
Vintage Horizons (see [Credits](#credits)).

Other VS LOD mods (Farseer, ChunkLOD) must be on the server. This one needs no server
install. It builds a persistent level-of-detail cache from the chunk data that your
client already receives as you play. It then draws that cache far past the normal view
distance. Coverage grows as you explore, and it persists across sessions.

You can also install it on your server. Then it shares its own cache with players and an
admin can build the horizon on request. It can also prepare a lightweight land silhouette far
beyond cached terrain. Neither server installation nor that feature is necessary for ordinary
client capture, and players without the mod are unaffected.

## What it does

- **Unlimited render distance**, decoupled from the vanilla view-distance slider.
- **Real 3D terrain**, not a heightmap. Mountains, overhangs, cave mouths, forests, and
  anything you build all appear at distance, at 1-block resolution near the player.
  Chiselled blocks, including the ones ruins are built from, are drawn in the material that
  shows most on their outside.
- **Translucent water**, drawn over the lake and sea floors beneath it.
- **Live seasons and winter cover**. Grass and foliage follow the game's own climate and
  season maps at the terrain's position. Loaded vanilla chunks also teach the cache their
  actual snow-covered surface, including snow layers and snow-covered block variants. Where
  that observation does not exist, the mod falls back to a position-specific seasonal snow
  line. Snow and frost are colour/material effects on the existing LOD surface; they add no
  snow-block geometry.
- **Persistent per-world cache** that grows as you play. Join time and memory use do not
  depend on the size of the explored area.
- **Optional Farseer horizon terrain** when the server also runs Doden's Landscapes. A sparse height
  field shows the broad shape of land beyond cached terrain and across unexplored gaps. It yields
  cell by cell only when ready vanilla or LOD geometry can replace it, slopes its edge down to hide
  unresolved gaps, and uses gentle slope/depth contrast to keep overlapping distant ridges
  readable. A natural, sunlit fog follows the exact Farseer/LOD boundary: a shallow strip lying on
  the seam hides the handoff, and a thin bank above it, which Farseer shows through, thins outward
  and frays at its edges, spilling across both sides and over water. Its outline follows the
  terrain, not the camera, and fog left behind as the seam moves dissolves instead of vanishing. It
  is independent of the real 3D cache, and Farseer and its fog can each be switched off completely.

## Recommended mods

- **[Algernon's Terrain Sampler](https://github.com/Algernon733/AlgernonsTerrainSampler)**, or
  **Watersheds** if you already use it, for Farseer. Either one lets the server work out distant
  terrain heights directly, so Farseer builds a faster, finer horizon. Without one, it builds slowly
  from the game's own terrain previews, which share time with world generation. Neither mod is
  required. Install it where Doden's Landscapes runs on the server; in singleplayer, that is your own
  game. Algernon's Terrain Sampler is required on clients too, so every player on that server
  downloads it. When neither is found, the Farseer tab in `.dlconfig` shows a red warning.

## What it cannot do

A client-side mod knows only the terrain that the server sent it. Land that you never
came near never reached your client. It is not in the cache, so the mod cannot draw it.
A new world therefore shows nothing past the vanilla view distance until you travel.

Server-side generators (Farseer, ChunkLOD) ask the world generator directly and do not
have this limit. In exchange, those mods must be on the server. Doden's Landscapes' optional
silhouette feature makes the same broad server-side trade for heights only; without the server
mod, ordinary cached terrain still grows solely from land the client receives.

In this mod, the edge of the explored area fades into the horizon. It does not end in a
cliff. The picture fills in the more you play. If you run the server, `/dlgen` builds it
in advance.

## Requirements

- Vintage Story 1.22.5 or later.
- A graphics card and driver with **OpenGL 4.3** (compute shaders and multi-draw indirect).
  Distant terrain is drawn entirely on the graphics card, and there is no older fallback.
  Macs do not qualify: macOS stops at OpenGL 4.1. Some older laptop graphics lack it too.
- On a machine that fails the check, the mod says so in chat and draws no distant terrain;
  the client log names what was missing. It keeps building its cache, so a later check that
  passes (`.dlgpu on`, or a driver update) shows everything you explored in the meantime.

## In-game commands

| Command | Purpose |
| --- | --- |
| `.dlinfo` | Status: cached/resident sections, meshes, current far edge, settings |
| `.dldiag [on\|off]` | Show or hide the diagnostics side panel for this session. Off by default. |
| `.dlconfig` | Open the settings window. **General** holds distance and visual settings; **Anti-Aliasing** holds every `.dltaa` dial; **Farseer** holds reach, outline, hill-softness, edge-apron, classic-look and seam-fog controls; **Generate** prepares and controls permanent world generation. Changes preview live where applicable, Save keeps them and Cancel restores the previous values. If ordinary in-game Settings has paused an integrated server, Generate offers **Open live controls** and hands off to this standalone window before sending server actions. |
| `.dldetail [auto\|manual\|target <px>\|<blocks>]` | Without an argument, report the six transition distances and where they come from. `auto` (the default) chooses them from your field of view and render resolution so every level hands over at the same size on screen, and `target <px>` sets that size (1.5 by default); `manual` returns to your own distances; a number sets a doubling sequence beginning there and switches automatic off. Saved; `.dlconfig` edits them individually. |
| `.dlfar <blocks>` | Cap the LOD render distance. `0` means unlimited, which is the default. |
| `.dltaa [on\|off\|show\|reach\|nudge\|sharpen\|blend\|still\|clip\|reset]` | Distant anti-aliasing: stops distant terrain shimmering as you move by nudging it a fraction of a pixel each frame and blending frames together, on cached terrain only. On by default and saved; also a switch in `.dlconfig`. `reach <px>` sets where it begins as the on-screen width of one block; `nudge`, `sharpen`, `blend` (memory while turning), `still` (memory when still) and `clip` (how strictly old frames must match) tune it. All are saved and all are sliders on the `.dlconfig` Anti-Aliasing tab; `reset` restores the defaults, `show` tints what it touches for the session. With no argument it reports its settings and GPU cost to chat and the client log. |
| `.dlsilhouette [on\|off\|log\|stats]` | Session-only Farseer override and status. The saved enable switch, 1,024–32,768-block reach, outline, hill-softness, edge-apron, colour/look, the Seam fog switch and twelve seam-fog dials are on `.dlconfig`'s Farseer tab. `off` drops the server demand and releases client tile, worker and GPU state; it does not rewrite the saved choice. |
| `.dlfog [on\|off\|stats]` | Session-only seam fog switch; no argument toggles it. Off draws no fog pass and builds nothing for it, so comparing frame rates on and off in one scene shows the fog's whole cost. The Farseer tab's Seam fog switch saves the choice. |
| `.dlmask [on\|off]` | Hand each loaded vanilla chunk its own ground instead of using one distance, and skip cached terrain that is entirely replaced. On by default since 0.3.17; the setting is saved. Turn it off to fall back to a single measured distance. |
| `.dlpaint [on\|off]` | Diagnostic. Paints the terrain the mask is hiding magenta instead of hiding it. A gap that turns magenta is the mask's doing; a gap that stays empty is not. Magenta can also show where distant terrain stands in front of the game's own, which is harmless. |
| `.dlskip [on\|off]` | Only matters with `.dlmask on`. On (the default) lets the mod drop a whole cached piece when the game covers all of it. Off keeps the per-pixel masking but never drops a whole piece. |
| `.dlholes` | Finds ground the mod handed to the game that the game is not drawing, anywhere around you. No need to look at anything, so it works for a band behind you. |
| `.dlgeom [on\|off]` | Only matters with `.dlmask on`. On (the default) delays a new handoff until vanilla holds uploaded terrain geometry. It does not revoke ownership after the handoff. Turn it off to compare. |
| `.dlwhy [blocks]` | Look at a hole and run this. It searches your line of sight (out to the mod's full draw distance by default) and reports the **first** patch of ground nothing is drawing, so whatever is visible behind the hole does not confuse the answer. It says whether the mod is hiding cached terrain it should not, whether the game claims that ground while holding none, or whether it was simply never captured. |
| `.dlsky [on\|off]` | Diagnostic for flashes and holes. After distant terrain is drawn, it counts sky showing below the horizon every frame and writes each flash to the client log with where it was and how the chunk mask stood there. `.dlsky verify on\|off` also reads the mask back from the graphics card after every upload. Off by default and not saved; it draws nothing. |
| `.dllaterange [on\|off]` | Checks the game's draw range again after it moves the player each frame, so a chunk leaving it passes to distant terrain in the same frame. On by default and not saved. `off` restores the one-frame-late timing of 0.4.13–0.4.52, for comparison only. |
| `.dlgap [on\|off]` | Diagnostic. Logs every chunk the mask gives to the game while the game is not drawing it, with the reason and how long it lasted. `.dlgap release on\|off` also hands such chunks to distant terrain, as a comparison. Not saved. |
| `.dlcoarse` | Standing where far terrain looks blockier than it should, run this. It reports which nearby detail is missing and what it is waiting for. |
| `.dlbackface [on\|off]` | Reject back-facing cached solid terrain before shading it. On by default and not saved; water and thin surfaces remain two-sided. |
| `.dlgpu [off\|on\|verify]` | Turn the GPU terrain renderer off or on. It is on by default and remembered. `off` releases regional buffers and hides cached terrain (there is no other renderer to fall back to); `on` also re-runs the graphics check; `verify` rebuilds the selected regional representation with upload readback checks. Run it without an argument for the effective path and arena numbers. |
| `.dlhzb [on\|off]` | Build the depth pyramid used to find terrain hidden behind hills. Measurement only - it hides nothing and the picture does not change. Off by default and not saved. Run it with no argument to see its state; the `hzb:` line in the log carries what it costs. |
| `.dlfront [on\|off]` | Submit cached solid terrain nearest first so nearer ground can hide farther ground sooner. On by default and not saved; water keeps its original order. |
| `.dlocclusion [on\|off]` | Draw cached terrain just after vanilla so the ordinary depth test rejects pixels behind current hills. On by default and not saved; `.dlocclusion off` immediately restores the pre-vanilla order. |
| `.dltemporal [on\|off]` | Stop submitting cached solid meshes that a completed GPU test found fully hidden. On by default with the aggressive profile and not saved; requires `.dlocclusion on`. |
| `.dltemporalprofile [safe\|aggressive\|extreme]` | Tune delayed occlusion live. `safe` discards results on camera turns, `aggressive` keeps the accepted performance while protecting seams and turning screen edges, and `extreme` exposes the maximum-performance/artifact limit. Not saved. |
| `.dlnear <blocks>` | How close to you distant terrain stops being drawn. `0` draws it right up to the camera, which is what you want when the game's own view distance is turned down far enough to see it. |
| `.dlexact [map\|audit]` | Use the game's own measurement of how far it draws each chunk instead of a safe estimate. `map` writes the measurement to the log; `audit` checks the mask against what the game is actually drawing, chunk by chunk. |
| `.dlcavecull [on\|off]` | Stop building cavities outside the surface-light and straight-sight envelope. On by default. Rebuilds distant terrain, which takes a few seconds. |
| `.dlsubtree [on\|off\|heights\|reset]` | Cull whole quadtree branches by how tall the terrain under them really is. On by default. No argument reports what it rejected. |
| `.dlslabs [surface\|near\|off]` | Draw terrain slabs at half height. `surface` is the default. Rebuilds distant terrain. |
| `.dlcutout [on\|off]` | Let crops, tall grass and vines hide the terrain behind them again. Off by default, which is correct — on reopens the holes at the seam it was added to fix. Rebuilds distant terrain. |

Server commands include `/dlworldgen start [radius] [circle|square] [here|spawn|coords] [x] [z]`,
followed by `/dlworldgen confirm`, plus `/dlworldgen pause|resume|stop|status`. They require the
`controlserver` privilege and generate permanent savegame terrain; see below. `/dlgen` remains the
separate transient horizon-only command.

### Making distant terrain match the game's

| Command | Purpose |
| --- | --- |
| `.dlblend [on\|off]` | The master switch for seam blending and level-0 block textures. Saved; also in `.dlconfig`. |
| `.dlband ...` | Blend the seam between game terrain and distant terrain. No argument reports the settings. `on \| off \| shape \| map \| speckle \| width \| step \| inner \| strength \| darken \| saturate \| sink \| overlap`. |
| `.dltexture ...` | Draw distant ground with the game's own block textures inside the blending band instead of one flat colour per block. `on \| off \| strength \| detail \| mips`. |
| `.dllight <part> [on\|off]` | Match cached terrain lighting to the game's. `sun \| moondir \| ramp \| ambient \| boost \| sky \| all`. All on by default. |
| `.dltoplight [on\|off]` | Light flat cached ground the way the game lights flat ground, rather than by sun angle. On by default. |
| `.dltint [block] ...` | Take one block's distant colour apart against the game's own answer for it, written to the log. Also carries the switches for per-area climate (`field`), per-tree seasonal variation (`leaves`, `leafwide`), and `leafcheap`, a measurement instrument that draws deliberately wrong colours. |
| `.dlclimate` | How far the client actually has climate data and how fast climate changes once it does. Decides whether distant terrain can be given its own biome colour or only the one under your feet. Written to the log. |
| `.dlwater ...` | How distant water meets the game's own. No argument reports the settings. See the table below. |

### Distant water

`.dlwater` with no argument reports everything below and writes it to the log.

| Part | Purpose |
| --- | --- |
| `off` / `<chunks>` | Suppress cached water within this many chunks of you. `off` is the default. |
| `edge <chunks>` | How far water's own footprint is grown past the ground's. Derived when unset. |
| `fresnel [on\|off]` | Give distant water the game's own opacity, which changes with the angle you look at it. On by default. |
| `alpha <0.05-2>` | Ceiling applied before the water texture and climate tint reduce opacity. `1.3` is the accepted default; values above 1 are useful because the mapped texture alpha still lowers the final result. |
| `depth [on\|off]` | Darken ground under water by how deep it is, as the game's own light does — a bed under eight blocks draws at 70% of the shore beside it, one under twenty at 26%. On by default. **Rebuilds every loaded section.** |
| `clarity <blocks>` | How far light gets back UP through the water to your eye. Smaller is murkier and darkens sooner. **This has no counterpart in the game** and cannot follow your viewing angle; see the caveat below. `40` is the accepted default; `0` turns it off. |
| `wave <blocks>` | How wide a strip of water keeps one quad per block so it can carry the game's own wave where the two surfaces meet. `1` is the default; `0` turns the wave off. **Rebuilds every loaded section.** |
| `walls [on\|off]` | Diagnostic. `off` drops the side faces drawn where the cache has no record of what is beside a body of water. |
| `glint <0-1>` / `foam <0-2>` | How much of the game's sun reflection and surface noise distant water carries. Foam defaults to the accepted `1.6`. |

**A caveat on `clarity`.** The game's own light only models sunlight travelling DOWN through
water, one level per block, and this mod reproduces that exactly. `clarity` is an extra term
with no equivalent in the game, added to compensate for holding cached water less opaque than
the game's formula would. Because it is baked in per block it cannot know the angle you are
looking through the water at, so a value that matches at one render distance will not
necessarily match at another. Treat it as a taste setting, not a correction.

### Keep one LOD mod switched on

Doden's Landscapes always draws distant terrain. It does not step aside for other LOD mods
such as Farseer, Distant Vistas, ChunkLOD, TopoHorizon or the original Vintage Horizons. Two
LOD mods drawing at once draw over each other: the horizon flickers, and you pay the
frame-rate cost twice. Keep only one switched on in the Mod Manager, and restart the game
after changing it.

When one of those mods is switched on, a window at every login names it. A server that runs
Farseer, ChunkLOD or TopoHorizon makes every client download it; switch Farseer off in its own
dialog (Ctrl+Shift+F) and it no longer counts. Without a known LOD mod switched on, a general
reminder shows at login until you choose "Don't show again". The client log names the same
mods, which is where a dedicated-server admin sees them.

### Smoother exploring: the Performance tab

The small freezes while exploring new terrain are .NET garbage collections. The settings'
**Performance** tab has one switch, off by default, that makes the game use every core for
them (.NET's Server GC): on the owner's PC they ran 15-25 times less often and the 1% low rose
from about 40-57 to 92-138 FPS. It costs about 0.3-0.5 GB of memory, and the first minute
after joining has a few longer pauses while the world loads. Saving it adds one line to the
game's own start-up file (`Vintagestory.runtimeconfig.json`), keeping a backup of the
original, and it applies from the next start. Switch it off before removing the mod. So far it
has been tried on one Windows PC.

### Server commands

Six server commands exist as well (`/`, not `.`). They need the controlserver
privilege, which every singleplayer host has. Coordinates are typed as the game's coordinate display
shows them, measured from spawn:

| Command | Purpose |
| --- | --- |
| `/dlserver` | Server assist status: settings in force, cache size, transfer counters |
| `/dlgen start [radius] [x z]` | Build the LOD cache around you (or around `x z`). It generates terrain that nobody visited yet. Also `stop` and `status`. See below. |
| `/dlworldgen start [radius] [circle\|square] [here\|spawn\|coords] [x] [z]` | Permanently generate and save real terrain in a bounded area, after `confirm`. Also `pause`, `resume`, `stop` and `status`. See below. |
| `/dlchunkrest [on\|off]` | Whether the game's chunk thread rests after a pass that found nothing to do, for this session; without an argument it reports the state. |
| `/dlrefresh [radius]` | Reload and recapture existing vanilla terrain around you so cached snow and edits catch up. Default 32 chunks, maximum 128. Missing terrain is never generated. Also `stop` and `status`. |
| `/dlsilhouetteserver [status\|compare\|presample]` | Silhouette builder/store/transport status, plus developer accelerator comparisons. `compare` and `presample` write measurement files; ordinary players need only `status`. |

The client settings persist in `VintagestoryData/ModConfig/dodenslandscapes.json`.
The per-world cache lives in `VintagestoryData/ModData/dodenslandscapes/<savegame-id>.db`.
When an incompatible update changes what the stored data means, the mod discards that cache.
Compatible additions are read conservatively—for example, old rows without an observed-snow
answer keep their terrain and use the climate fallback until recaptured.

### Sharing the server's cache

A server with Doden's Landscapes shares its cache with players who have the mod. The settings
are in `ModConfig/dodenslandscapes-server.json`: `EnableServing` (default `true`),
`ServeRadiusBlocks` (default 8,192 blocks; 0 is unlimited), `MaxSectionsPerSecondPerPlayer` (8)
and `MaxSectionsPerSecondTotal` (32).

The radius limits which terrain a player is sent, not the list of what the server holds. Every
player with the mod receives that list for the whole world, in 64×64-block pieces, and a notice
whenever one of them changes; this is how a returning player catches up on what changed while
they were away. The mod never shows that list, but a modified client could use it to see where
players have explored and where terrain is being built or dug, anywhere on the server. On a PvP or
raiding server where that matters, set `EnableServing` to `false`: players then build their own
caches from what they see, as on a server without the mod.

### Horizon silhouettes

Silhouettes need Doden's Landscapes on both the client and server; integrated singleplayer counts as
both. They are automatic in integrated/LAN play. A dedicated server stays off unless its admin sets
`EnableSilhouettes` to `true` in `ModConfig/dodenslandscapes-server.json`. The same file controls
`SilhouetteReachBlocks` (the dedicated reach ceiling), `SilhouetteUseAccelerators`, and
`SilhouettePreviewsPerSecond`.

The server keeps heights only and writes no terrain to the savegame. Real captured heights are best;
selective engine previews refine them. If installed, Watersheds' sampler supplies the broad field;
Algernon's Terrain Sampler is accepted when Watersheds is absent. Neither mod is required, and a
sampler failure falls back to the standalone engine-preview path for that session. When a server
offers silhouettes from engine previews alone because it found neither mod, the Farseer tab shows a
red warning recommending one. No warning is shown when `SilhouetteUseAccelerators` is off, because
that is the admin's choice, or when the server runs a build too old to report this. Silhouette tiles
have their own server store. Since 0.4.88, clients lazily keep authoritative replies in a per-world
store for instant rejoin; switching Farseer off closes that store and clears its live state.

Farseer is fallback scenery, not a second copy of loaded ground. It stays present while replacement
terrain is still being prepared, then yields on the exact 32-block cells covered by ready vanilla
or LOD geometry at any LOD level. Its edge apron is adjustable (384 blocks wide by default); it
slopes only the nearby edge instead of lowering the entire distant field.

The remaining handoff is concealed by a separate atmospheric volume centred on that exact
ownership line. It has real height, extends below the sampled surface for cliffs and shores, spills
across both terrain systems, and composites after water so the veil also covers a shoreline or
water-chunk boundary. A strip only a few blocks deep lies on the seam and hides the step from any
angle; it rests on the ground the camera actually sees, so a slope neither buries it nor leaves it
floating. The bank above and around it is thin enough to see Farseer terrain through, thins
steadily away from the seam and breaks into wind-stretched strands, with a faint haze beyond. The
sun lights it: brighter tops, cool grey undersides, never brighter than the horizon. Its outline is
built from all Farseer terrain within reach, so turning the camera does not move it. When cached
terrain loads in and the seam moves on, the fog it leaves behind thins and frays away over the fade
time while fog over the new seam appears at once. It is marched at half resolution and rebuilt at
full resolution without spreading across near objects. The Farseer tab has a Seam fog switch and
exposes Seam strip density and height, Fog density, coverage, spread, height, floor depth, billow
size, drift speed, Fog fade time, Fog quality and edge softness.

## Building

Requires the .NET 10 SDK and a Vintage Story 1.22.5 or 1.22.6 install. Those are the two
versions the check suite runs on. The mod declares 1.22.5 as its minimum, so it will not
load on anything older.

```sh
export VINTAGE_STORY="$HOME/Games/vintagestory1.22.5"   # your game path
dotnet build VintageHorizons
```

The build assembles a loadable mod folder at `VintageHorizons/bin/Debug/net10.0/Mods/dodenslandscapes`.
`scripts/package.sh` produces a ModDB-ready zip in `dist/`.

### Offline savegame measurement

Three read-only tools answer questions about a world and its LOD cache without launching the game.
None of them writes to a savegame; a settled snapshot (the game closed, or a frozen copy) gives a
clean answer, and a world mid-session does not.

```
scripts/savegame-churn.py BEFORE.vcdbs AFTER.vcdbs      # what changed between two snapshots, and why
dotnet run --project scripts/fingerprint-dump -- WORLD.vcdbs out.tsv
scripts/claim-check.py out.tsv <worldid>-server.db --by-minute
```

`savegame-churn.py` groups `chunk` rows into columns and decodes the differing ones into protobuf
fields, so a column that moved only because a berry bush grew is reported separately from one whose
blocks changed. `fingerprint-dump` writes the mod's own column fingerprints by calling
`LodSavegameScan` itself rather than copying its rule, and `claim-check.py` compares that dump
against the claims stored in a cache's `ColumnFingerprint` table - which is exactly what the
background audit does in game.
Before creating or installing any changed playable artifact, increment the patch version
by exactly one in both `modinfo.json` and the project file. Ordinary compile and check runs
do not consume a version number.

```sh
scripts/dev-run.sh              # opens/creates the "vhsurvival" test world
scripts/dev-run.sh myworld      # a different world
```

### Savegame sweeping

A savegame holds every chunk column that anyone ever generated. The LOD cache saw only
the part that streamed past a player who ran this mod. The sweep loads those columns so
that capture sees them. You get a horizon over every place that you visited before, and
you do not fly back over it.

Sweeping is on by default, in singleplayer too. The settings are `SweepSavegame`,
`SweepRadiusChunks`, `SweepColumnsPerSecond` and `RefreshColumnsPerSecond`, in
`ModConfig/dodenslandscapes-server.json`.

The default is safe because the sweep **generates nothing**. The sweep skips a position
that nobody visited. It also skips a border around the explored terrain, because a
load there makes the engine generate the missing neighbours.

`PregenRadiusChunks` takes the opposite trade. It creates terrain that nobody visited yet, so
it stays off unless an admin asks for it. That setting now uses the same transient
generation `/dlgen` uses, so it too writes nothing to the savegame.

### Keeping dormant snow current

Loaded chunks update through vanilla's ordinary chunk-dirty events, so terrain near active
players is recaptured when snow accumulates or melts. Previously explored terrain that is no
longer loaded uses a slower server-side path after the startup sweep:

- Every 30 seconds, the server reads vanilla's public regional snow-accumulation snapshots.
- A region crossing an accumulation or melt band starts one finite campaign over eligible
  existing columns. The default rate is one chunk column per second; the configurable range is
  0–16, and 0 disables automatic snow refresh.
- One dormant map region is woken per 30-second poll as a canary, so an unloaded region
  eventually exposes new weather snapshots. Columns are not cycled continuously between
  weather changes.

A “column” here is one 32×32 vanilla chunk footprint through the full world height. The
scheduler uses the generated-column map built by the safe sweep and never peeks or generates
missing terrain. There is deliberately no “can accept snow” flag: an observed column records
how many surface samples were snow-covered, and permanent warm/cold regions become ineligible
for the direction that cannot change them.

`/dlrefresh [radius]` is the manual catch-up path. It performs the same two-pass existence and
safe-neighbourhood proof as the sweep, then recaptures only existing columns. Updated level-zero
rows are offered to already connected modded clients after they are durably stored; integrated
singleplayer detects the same replacement through the sibling cache's incremental rowid offers.

### Generating the real world (/dlworldgen)

`/dlworldgen` deliberately creates complete vanilla terrain and saves it in the world, like
vanilla pregen. It is different from `/dlgen`, which builds only a transient, bare LOD picture and
does not add terrain to the savegame. Because this operation is permanent, `start` only prepares
the plan and reports its area, estimated time and disk use; `confirm` starts it after you have read
the warning and considered a backup.

The default is a circle around the player. Generation begins with the player's 16×16-chunk tile,
moves outward through circular radial bands clockwise, and handles the nearest columns first inside
each tile. Large tiles let the distant-terrain cache save efficiently, while the overall reveal
grows outward in the same broad shape as ordinary map exploration. Choose `square`, `spawn` or
explicit `coords x z` when needed; x and z are the numbers the game's coordinate display shows,
measured from spawn and negative where it shows them negative.

The job can pause, resume and stop, and stores durable progress so an interrupted job can be offered
for manual resume after restart. It holds only bounded work in flight and slows itself when the
engine queue, capture path, distant-terrain saves, server load or free disk space require it. The
Generate tab shows progress and the active gate. In integrated singleplayer, ordinary Settings
pauses the server; use its **Open live controls** handoff or open `.dlconfig` directly.

Only the Balanced behavior is exposed. Vintage Story's optional multi-thread pipeline was tested
with its real `MaxWorldgenThreads` setting and named threads, but it gave no material speedup and
made generated chunk contents scheduling-dependent, so Doden's Landscapes does not offer a thread
selector or Fast/Maximum presets.

Server bounds live in `ModConfig/dodenslandscapes-server.json`:
`EnableWorldGeneration`, `WorldGenMaxRadiusChunks` (default 256),
`WorldGenDefaultRadiusChunks` (32), `WorldGenQueueTarget` (120),
`WorldGenMaxInFlight` (160), `WorldGenDirtySectionLimit` (512), and
`WorldGenOldestDirtySeconds` (120).

### Generating the horizon (/dlgen)

Sweeping and capture cover only terrain that exists. `/dlgen start [radius] [x z]` covers
the rest. It builds the LOD picture around you, or around coordinates you give (as the game's
coordinate display shows them), for land that nobody visited yet. It writes **nothing** to the
savegame.

The engine's `PeekChunkColumn` runs real worldgen from the seed and returns the column
transiently. The mod captures it and throws the terrain away. A column that already
exists loads normally instead, so player builds stay correct.

Every run then re-probes a sample of the positions that it generated. It reports the
result on its finish line, as "Verified 256/256 sampled absent positions still absent".
The check regimen also asserts the savegame promise byte for byte, but only against
vanilla worldgen. The runtime check is what watches it on a modded server.

**Generated terrain is bare.** It carries the landform, the rock strata, caves, rivers,
and the soil and sand on top. Nothing else. A comparison with a full generation of the
same column shows that a peek misses 67 block types. The missing types are snow, ore,
cave dressing, surface boulders, small water, worldgen ruins with their contents, and
trees in a forested column.

What a peek produces is never wrong. Nothing appears in a peek that a real generation
does not also make. So generated terrain reads as a correct but plain version of itself,
and real capture fills in the rest the first time a player visits. `/dlgen diff` prints
the measurement for your own world.

It stops there because the pass that adds trees crashes vanilla worldgen when it runs
this way. The fix is a patch this mod does not ship.

A connected modded client learns newly durable server sections and replacements through bounded
follow-up offers. On a server without Doden's Landscapes, the client still learns only from vanilla
chunks the server actually sends it.

The command needs the controlserver privilege. Five settings bound it in the server
config: `EnableGenerateCommand`, `GenerateMaxRadiusChunks` (the ceiling, default 128),
`GenerateDefaultRadiusChunks`, `GenerateColumnsPerSecond` and `GenerateMaxInFlight`.

### Running the checks

```sh
scripts/check.sh              # all three tiers, in order (~25 min)
scripts/check.sh fast         # pure logic and static assets, no game (~30 s)
scripts/check.sh smoke        # one end-to-end sandbox run (~5 min)
scripts/check.sh matrix       # install combinations and admin controls (~20 min)
```

Run `fast` constantly. It needs no game process, and after the first build it finishes in
less than a second. Run all three tiers before you commit.

The three tiers answer different questions.

`fast` covers the pure logic: key packing, the RLE column store, mip downsampling, and
the mesher's greedy merge and coverage rules. The blob format, frustum planes and config
clamps are in it too. It also covers the invariants that span files, which no compiler can catch. One
example is the shader's `TINT_SLOTS` matching `LodTintRegistry.MaxSlots`.

`smoke` starts a vanilla dedicated server and a sandboxed client, then asserts on what the
run logged. It includes a second pass against the warm cache. That pass is the only way
to know that the client can read back what it wrote.

`matrix` covers the configurations that other people put the mod in. It runs a vanilla
server, a modded one, and a client without the mod at all. It also tests each admin
switch. Two scenarios cover competing LOD mods: `lod-conflict` proves that we keep drawing
beside one that is switched on and name it in the log, also when its switch file cannot be
read, and `farseer-off` proves that we do not name one the player switched off.

**There is no CI, and there cannot be.** Building this repo requires the Vintage Story
assemblies from a local game install, and those are not redistributable, so no hosted
runner can compile it. `scripts/check.sh` is the entire safety net.

### Testing without touching your own game

Test instances run in a `.testdata` sandbox and must be started and stopped only through
these scripts:

```sh
scripts/test-server.sh                              # vanilla dedicated server, port 42425
scripts/test-client.sh -c localhost:42425           # sandboxed client
scripts/test-stop.sh [client|server|all]            # stop via pidfiles
pwsh -File scripts/test-stop.ps1 all                 # the same, on Windows
```

CAUTION: Start and stop test instances only with these scripts. The VS client allows one
instance, through a named pipe in `$TMPDIR`. A launch with `-c` **forwards its connect
request into whatever instance is already running**, including your own game, and then
exits without a message.

`--dataPath` does not protect you. A private `TMPDIR` does, and these scripts set one up.
They also record the child PID, and they make sure that `/proc/<pid>/cmdline` names the
sandbox before they signal anything. Without that step, a stale pidfile and a reused PID
together kill an unrelated process.

Useful env variables for unattended runs: `VINTAGEHORIZONS_AUTOUNPAUSE=1` (game ticks
continue without window focus), `VINTAGEHORIZONS_AUTOEXPLORE=1` and
`VINTAGEHORIZONS_EXPLORE_HOP=<blocks>` (teleport along a spiral so that fresh chunks
stream in). `VINTAGEHORIZONS_GPU_STATS=1` enables delayed GPU timers for the cached opaque
and water passes. The timers poll only results a later frame reports available; leave the
variable unset for a control run with no timer-query submission. The Windows runner's
`-GpuStats` switch pins that value and records it in the scenario JSON.

Renderer development also accepts `VINTAGEHORIZONS_GPU_RENDERER=off|shadow|auto`. `off` is
the default. In the current Phase 1 implementation, every value keeps legacy as the only
visible renderer; `shadow`/`auto` can only enable a validated CPU metadata mirror that owns
no GL resources and submits no draws.

The Windows benchmark runner requires PowerShell 7 and uses the same isolated
client/server setup. Fixed route entries hold one position and camera angle; trajectory
entries add `->` plus an end position and angle, which drives deterministic continuous
movement during the measure interval. For example:

```powershell
pwsh -File scripts/bench-windows.ps1 -Label moving-rotation `
  -Route bench/routes/moving-rotation.txt -Measure 30 -Laps 2
```

Phase 9 fallback tests add `-GpuFailure arena|shader|depth-copy|draw`. The runner pins the chosen
one-shot fault through `VINTAGEHORIZONS_GPU_INJECT_FAILURE` and records it as `gpuFailure` in the
scenario JSON. Use this only in an isolated test launch: it deliberately disables a renderer stage
for that session.

For format-only comparisons, pin `-GpuClusters 0` in both halves and change only
`-GpuPacked 0|1`; clusters consume packed geometry, so leaving them enabled would change two stages.
The runner records current split-near/split-far GPU timers, upload tail latency, and live/committed
bytes for expanded, packed, and clustered arenas. A completed route with missing telemetry arrays is
a parser failure, not usable evidence.

The bundled moving route targets active streaming/capture, renderer traversal, far-plane
stability, and turn-around behavior. Warm-up laps make later measured laps the warmer
comparison; use the fixed `vhsurvival.txt` route when measurement needs stationary views.
Use `-Cooldown <seconds>` when a scenario needs the final endpoint held after measurement
so queue convergence and shutdown-ready telemetry can be observed before completion.
Add `-RequireMipConvergence` with at least a 30-second cooldown to make the run fail unless
the final sampled client state has no capture input/results, mip queue/in-flight work,
worker errors, unsaved sections, asynchronous loads, or storage backlog/errors. The
scenario JSON preserves the parsed final state.

Crash recovery is a guarded two-run variant. The first run uses
`-InterruptWhenPersistedMip`; its storage worker publishes a marker only after writing an
`ApplyToParent` row, and the runner then terminates only the pidfile-verified sandbox
client while leaving its isolated server available. The second run uses `-ReuseServer`,
`-RequireMipRecovery`, `-RequireMipConvergence`, and a cooldown of at least 30 seconds. It
must load one or more persisted mip obligations and converge every ordinary pipeline and
storage guard to zero before stopping the server normally.

Pass `-IntegratedSingleplayer` to launch a named world without a separate server process.
Integrated runs use `.testdata/integrated`, so their save, caches, logs, pidfiles, and
single-instance pipe remain separate from both ordinary game data and dedicated-process
benchmarks. `-WorldName` selects the sandbox world. The interruption run needs no
`-ReuseServer`; recovery reopens the same world, and `-RequireNoPersistedMips` guards a
third fresh-process postcheck. `-RequireLocalOfferRetry` enables one sandbox-only transient
sibling-cache miss and requires that exact section to install later. A pinned
`-ServerConfig` is copied into the integrated data path.

Pass `-ServerMod` to install Doden's Landscapes on the isolated server as well, which makes
sweep and server-assist phases available to the run. `-AutoCommand "/dlgen start 8"` can
start a bounded generation scenario after join. Stats and managed-allocation sampling are
enabled by default; use otherwise identical runs with and without `-DisableStats` to
measure their overhead. The portable `scripts/bench.sh` runner exposes the equivalent
`--server-mods` and `--no-stats` controls.

For a warm-cache join, use `bench/routes/warm-cache-join.txt`, no warm-up lap, and
`-ClientCache Warm`. The runner refuses to start without a sandbox database, verifies the
active world reports more than zero cached sections at level finalization, and writes the
proof to `<label>-scenario.json` beside the CSV. `-ClientCache Cold` provides the inverse
non-destructive guard for a cache that was moved aside manually.

For a completed sweep, combine `bench/routes/completed-sweep.txt`, `-ServerMod`, and
`-ServerConfig bench/configs/completed-sweep.json`. `-RequireServerText "Savegame sweep
finished:"` makes a partial run fail and preserves the matching terminal line plus the
pre-launch server-cache state in the scenario JSON. A pinned server config requires a
fresh isolated server; the runner refuses to apply it to `-ReuseServer`.

For completed transient generation, use `bench/routes/completed-generation.txt` with its
pinned config and `-AutoCommand "/dlgen start 8 520000 520000"`. The
`-RequireGenerationComplete` guard parses the terminal counters and requires real generated
columns, zero timeouts and height-map failures, and a non-empty sampled-absence check whose
positions all remain absent from the savegame.

For a saturated live-assist transfer, use `bench/routes/saturated-assist.txt`, its pinned
config, `-ClientCache Cold`, `-ServerCache Warm`, and
`-RequireAssistPeakInFlight 16`. The runner verifies the active server cache rather than
trusting a database filename, then requires all normal request slots to have been occupied
and at least one server section to have been received and installed.

### Development notes

- **Shaders must be pure ASCII** (even comments). The engine's OpenTK marshaling
  truncates the GL source by the difference between UTF-8 bytes and char count,
  which silently cuts the end off the shader.
- Worlds created via `-o` default to the `creativebuilding` playstyle, which is
  **superflat** - the dev script passes the `preset-surviveandbuild` lang code for real
  terrain.
- Singleplayer pauses while the game window is unfocused, which stops game ticks - and
  with them LOD ingestion and cache saves.
- The game's block registry must not be read off the main thread (`GetBlock(int)` lazily
  mutates a dictionary). Sections deserialized on the storage thread keep their palette
  block *codes* and have ids resolved at install time on the main thread.

See [dev/ARCHITECTURE.md](dev/ARCHITECTURE.md) for the current architecture and settled
invariants. [STATUS.md](STATUS.md) records current evidence and uncertainty,
[dev/TODO.md](dev/TODO.md) owns open work, and the approved performance sequence is in
[dev/plans/PLAN_MAIN_THREAD_PERFORMANCE.md](dev/plans/PLAN_MAIN_THREAD_PERFORMANCE.md).
[DESIGN.md](DESIGN.md) remains the historical design journal and research-provenance
record. See [CHANGELOG.md](CHANGELOG.md) for released changes and
[docs/RELEASING.md](docs/RELEASING.md) for the release procedure.

## Credits

- Vintage Horizons (MIT, © AliasFactory) - the mod this one began as a fork of, and the
  original base it was founded on.
- [Distant Horizons](https://gitlab.com/distant-horizons-team/distant-horizons) and
  Voxy (Minecraft) - architectural inspiration. No code is used from either.
- [Farseer](https://github.com/ViciousBadger/VSMod-Farseer) (MIT, © Badgerson) -
  Vintage Story rendering techniques. Adapted code is credited where it is used.
- [Algernon's Terrain Sampler](https://github.com/Algernon733/AlgernonsTerrainSampler) and
  Watersheds - optional server-side height accelerators called through their installed APIs; no
  code is copied and neither is required.

## License

Copyright © 2026 Doden. Doden's Landscapes is licensed under the
[GNU General Public License version 3](LICENSE) only, with an additional permission that
allows combining it with Vintage Story and the libraries the game ships with; see
[NOTICE](NOTICE).

You may use, study, change and share it. If you publish a modified version, or another
work that includes this code, it must be released under the same licence with its full
source code.

It contains work by AliasFactory (Vintage Horizons) and Badgerson (Farseer) released under
the MIT licence; their notices are reproduced in [THIRD-PARTY-NOTICES](THIRD-PARTY-NOTICES).
