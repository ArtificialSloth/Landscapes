# Handoff - horizon silhouettes at 0.4.84, waiting on the owner's P0.3 verdict

**Updated:** `2026-09-15`, Session 95
**Branch/source checkpoint:** `codex/water-depth-light`, 0.4.84 at `8cd445f`; nothing pushed
**Installed:** `0.4.84` private test build, copy-installed. It is not tested in game; players see 0.4.82's behaviour.
**Last accepted baseline:** `0.4.64`
**Compatibility:** assist protocol / blob / schema `1 / 4 / 6`, unchanged. The silhouette tile store has its own schema `1`.

Read, in this order:
1. this page;
2. [Session 95](../sessions/SESSION_95.md), then [Session 94](../sessions/SESSION_94.md);
3. [the silhouette plan](PLAN_HORIZON_SILHOUETTES_AND_FRONTIER.md), its "How the build differs" note
   first;
4. [the gotchas](../GOTCHAS.md) G199–G208;
5. [TODO](../TODO.md).

`CLAUDE.md` holds the working rules; follow them.

## Where things stand

- **Built.** P1 (the first look) and P2 (captured heights, the fill and the tile store). P3 has
  begun.
- **Where silhouettes run.** Singleplayer and LAN-hosted worlds, behind `.vhsilhouette on`.
  Dedicated servers stay off, except for a developer switch used for measurement (below).
- **Height sources.** Captured columns and engine previews. No accelerator is used yet.
- **P0.3.** The accelerator comparison is built and measured. **The owner's verdict (D8) is
  pending.** They have the side-by-side page from run `p03-cold`.

## The P0.3 evidence

| Run | Build | Cells compared | Error median / 95% / max | Lean | Land/sea wrong |
|---|---|---|---|---|---|
| `p03-first` | 0.4.83 | 1,680 chunk corners | 1 / 5 / 21 | +1.5 | 11.6% |
| `p03-wide` | 0.4.83 | 4,096 chunk corners | 2 / 5 / 11 | +1.9 | 5.9% |
| `p03-cold` | 0.4.84 | 270,336 cells, every 4 blocks | 1 / 4 / 63 | +1.4 | 3.7% |

- **Before the land existed.** In `p03-cold`, answers taken in an earlier session matched the later
  answers at all 270,336 cells. Each sampler answered 270,400 cells in about 1 s, and the first sample
  in a new region cost under 1 ms.
- **One function.** With Watersheds installed, Algernon's Terrain Sampler and Watersheds' sampler are
  one height function, so they give identical figures (G205).
- **Sea level.** 82 on these test worlds (G208); why is not traced.
- **By the picture (not traced).** River valleys read high, because real generation carves channels
  last. Some lakes become low land.
- **Big errors are rare and on land.** 0.16% of cells are 8 or more blocks off, and almost all of
  those are on land or at the coast.

## How to run the comparison

- **Permission.** The owner allows the headless test servers the work needs, but not their client or
  their worlds. Check that no Vintage Story is running; the script also refuses to start.
- **Build first.** Package a new version first, following the version rule in `CLAUDE.md`. The script
  installs `dist/vintagehorizons_<version>.zip` for the project's current version.
- **PowerShell 7.** The scripts need it. On the owner's machine it is installed but not on the path,
  so call `pwsh` by its full path.
- **Preview.** `pwsh scripts/silhouette-compare.ps1 -PlanOnly` lists the mods it would copy: the
  owner's enabled mods, matched by `modid@version`.
- **Run.** `pwsh scripts/silhouette-compare.ps1 -Profile <new name> -Range 32` runs:
  1. the presample session, then a stop;
  2. pregen and compare in a second session, then a stop;
  3. the renderer, which writes the HTML page.

  Range 32 takes about 10 minutes.
- **Outputs.** Report lines go to `compare-report.txt` in the profile. The page and panels are written
  under `.testdata/silhouette-compare/<profile>/ModData/vintagehorizons/silhouette-compare/`.
- **Server commands** (controlserver): `/vhsilhouetteserver [status|compare|presample]`.
- **The measuring switch.** `VINTAGEHORIZONS_SILHOUETTE_MEASURE=1` keeps heights on a dedicated
  server without offering silhouettes (G206).

## Next work

1. **Get the owner's D8 verdict.** Ask plainly; they judge the page by eye.
2. **If accepted, P3 continues:**
   - use Watersheds' sampler for silhouette heights where Watersheds is installed. The origin order
     stays captured, then preview, then accelerator (plan 4.2), and a failure falls back to
     previews;
   - keep previews for coasts and lakes (coast refinement, plan 4.5);
   - decide whether to subtract the lean;
   - use Algernon's Terrain Sampler only where Watersheds is absent, after measuring it on such a
     world;
   - measure sampling beside real generation (thread safety, plan 3.2).
3. **If not accepted:** previews stay the only far source, and P3 continues with the transport and
   the dedicated opt-in.
4. **Everything else in P3** is listed in TODO.

## Before touching this

- **Traps.** G199: previews are invisible to the engine's generating count. G203: a preview rebuilds
  its map region. G204: a busy world-generation thread delays pausing and quitting. G205–G208: the
  sampler, dedicated-server, rain-count and sea-level traps.
- **Docs are the owner's record.** Do not edit them until asked. Commit only versioned test builds
  without asking. Never push.
- **Talking to the owner.** They are not a developer: use plain language and short, specific
  questions.

## Open, not established

- Why lakes and river beds differ.
- Why sea level is 82, and whether the owner's own worlds share it.
- Mountains are thin in the evidence: both measured areas lie at the world centre.
- The `bodanboys` preview stall (G204), which the owner has set aside.
- Not yet observed:
  - the joins;
  - stored-tile read-back after a restart;
  - the error brake;
  - P2's integration gate;
  - GPU cost and the off-state A/B;
  - a dedicated server with players.
