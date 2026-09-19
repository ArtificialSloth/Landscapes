# Session 102 — measured savegame-sweep contention and player-aware yielding

**Date:** `2026-09-19`
**Branch/commit:** `codex/water-depth-light` / `b2fe181ed770bf91dd2700e368a8b09ee4b9adc5`
**Mod version:** `0.4.111`
**Assist protocol / blob / schema:** `1 / 4 / 6`

> Session records are Tier 3 history. This session follows the formal scheduling analysis in
> Session 101 and records the controlled runtime evidence, the 0.4.111 scheduling change, and the
> remaining boundary of that evidence.

## Context and investigation

The owner reported that terrain felt slower to load while exploring and asked for recommendations
about sweep scheduling, yielding, and cheap ways to decide that a sweep is unnecessary. Session 101
had already established the mechanism: the startup sweep repeatedly probes and priority-loads saved
terrain, and the installed engine drains that work before its ordinary bounded generation slice.
What it had not established was the player-visible magnitude or which sweep phase mattered.

The owner authorized launching the game for measurement. The comparison used an integrated
singleplayer client, the same frozen `BodanBoys` save, the same client/server LOD caches, the same
mod list and the same deterministic route for every condition. The harness inputs are:

- `bench/configs/sweep-ab-on.json`;
- `bench/configs/sweep-ab-off.json`;
- `bench/routes/sweep-active-exploration.txt`;
- the seeded-profile support in `scripts/bench-windows.ps1`.

Every run restored the frozen profile first. This prevented one condition's save or cache changes
from becoming the next condition's starting state. The game processes were closed after testing.

## Work narrative

### 1. The probe pass showed only a weak tail signal

The first pair ran the route while the sweep was still testing savegame existence. The enabled run
had a 136.0 FPS 1% low and a 7.35 ms 1% frame-time tail; the disabled control had 159.3 FPS and
6.28 ms. Average FPS moved in the opposite direction, 487.5 enabled versus 476.2 disabled, and the
medians were 525.9 versus 507.1. That is a plausible tail penalty, but the mixed average and one
pair of runs do not establish a probe-phase regression. No probe scheduling change was made.

### 2. The load pass established downstream contention

The second pair held the route until the sweep was in its priority-load phase.

| Condition | Average FPS | Median FPS | 1% low | 1% frame-time tail | Managed memory | RSS |
|---|---:|---:|---:|---:|---:|---:|
| Sweep enabled, original scheduling | 315.5 | 351.3 | 88.3 | 11.32 ms | 4,848 MB | 8,820 MB |
| Sweep disabled | 316.4 | 340.4 | 110.3 | 9.07 ms | 4,510 MB | 7,578 MB |

Average throughput was effectively identical. The enabled condition's 1% low was 19.9% worse,
its 1% frame-time tail was 24.8% worse, and RSS was 16.4% higher. This is the controlled evidence
Session 101 required: the load phase can compete with player terrain work even when average FPS
does not reveal it.

### 3. The engine queue counter could not identify saved-column demand by itself

Two experimental gates used `CurrentGeneratingChunkCount` as the primary signal. A threshold of 8
never held during the measured saved-terrain route and produced a 89.3 FPS 1% low, 11.20 ms tail
and 8,853 MB RSS. Lowering the threshold to 1 still did not observe ordinary saved-column priority
loads at the scheduler's sampling cadence; it produced a 93.6 FPS 1% low, 10.68 ms tail and
8,922 MB RSS.

The counter did rise later when the route caused actual world generation. It therefore remains a
useful secondary guard at the first visible generation request, but it is not a sufficient primary
signal for player terrain streaming.

### 4. 0.4.111 yields to direct player terrain activity

The selected scheduling signal is a new or rejoining online player, or an online player crossing a
chunk boundary. Either event opens a five-second quiet window during which the startup load pass
issues no new priority loads. Movement by any player extends the window. A server with no players,
or players who remain in one chunk for five seconds, permits the sweep to continue at its configured
rate.

The existing fractional allowance still accrues only to its cap. A pause therefore cannot create a
catch-up burst. The existing 32-column capture brake and complete-neighbourhood safety rule remain
unchanged. `CurrentGeneratingChunkCount >= 1` is retained as a secondary world-generation guard.
The status and periodic telemetry now distinguish player-activity holds, engine-pressure holds and
the observed engine-queue peak.

The follow-up run measured 324.6 average FPS, 353.9 median, 109.0 FPS 1% low, a 9.17 ms 1% tail,
5,696 MB managed memory and 8,362 MB RSS. Its tail result is effectively the sweep-off control's
110.3 FPS / 9.07 ms. Telemetry recorded 1,621 player-activity hold ticks and 209 engine-pressure
hold ticks. Useful sweep work still ran during roughly one minute of quiet loading before the
measured route; movement then gave terrain streaming priority. There were no capture drops or
capture errors.

### 5. The route was mixed, not purely previously generated terrain

Every comparison followed the same route from the same frozen save and cache. Most of its
centerline, from chunk X 8000 through the endpoint at X 8050, already existed. A direct read-only
before/after comparison of the final save nevertheless found:

- `mapchunk` rows increased from 62,098 to 62,276: **178 new map columns**;
- `chunk` rows increased from 748,684 to 750,820: **2,136 rows**, exactly 178 columns times the
  world's 12 vertical chunks;
- the new map columns occupied X 8053–8062 and Z 7990–8012, beyond and around the endpoint through
  the player's view-distance halo.

The run therefore covered mostly saved-terrain streaming and then real world generation near the
end. That strengthens the conclusion that 0.4.111 visibly yields when actual generation begins,
but it does not isolate sustained travel through virgin terrain. A frontier-specific A/B remains
the next scheduling measurement.

### 6. Relationship to the terrain-change plan

The scheduling change and the terrain-change plan answer different questions. Version 0.4.111
decides when an already-required priority load should wait for players. Phase A of the plan decides
whether that load is needed at all. Its fingerprints, clean-shutdown marker and save-file size/time
check are the stronger shortcut for unchanged worlds; its changed-column path still needs the
0.4.111 scheduler for first adoption and real mismatches.

Elapsed game time and player count are not correctness signals. Time can pass without any terrain
change, and terrain can change immediately. A singleplayer session can change terrain through the
local player, simulation, commands or other mods. In multiplayer, the mod's live hooks see loaded
changes while it is active; after an unwatched or crashed interval, the savegame fingerprint is
what proves which columns changed. Player count and movement remain useful for scheduling because
they estimate current demand, not because they prove stored terrain is current.

Codex recommendations were appended to the original terrain-change plan in a separately labelled
section for independent review. They do not revise the owner's original phases or acceptance
criteria.

### 7. Verification and artifact

- Focused allowance/activity checks passed 26 assertions.
- The full fast tier passed **25,190 assertions with zero failures**.
- The Release mod build completed with zero warnings and zero errors.
- `dev/DocCheck.ps1` passed **10,753 documentation checks** with zero failures.
- `dist/vintagehorizons_0.4.111.zip` contains 43 root-relative entries and is 668,783 bytes.
- Its SHA-256 is
  `06BE22051686FF8F865B53557920D2037AB46141CA4B8D8541F36FAD2961D1AA`.
- The package was copied beside the owner's rollback builds and its installed hash matched.
- The exact source/test/version checkpoint is `b2fe181`.
- No protocol, section-blob, section-database or silhouette-store meaning changed.

---

## Delivered

- A controlled frozen-profile sweep-on/sweep-off comparison for both probe and priority-load phases.
- Direct evidence that load-phase contention harms tail latency while leaving average FPS nearly
  unchanged.
- Version 0.4.111's five-second player terrain-activity yield, secondary engine-generation guard,
  no-catch-up behavior and named telemetry.
- Focused and full automated verification, a verified/installed private test artifact, and Git
  checkpoint `b2fe181`.
- A direct save-database audit proving the route included 178 genuinely new map columns late in the
  run.
- Documentation that preserves the original terrain-change plan and labels the new recommendations
  as Codex proposals awaiting independent evaluation.

## Decisions

- Use player chunk movement/rejoin as the primary short-term scheduling signal for the startup load
  pass. It directly covered saved-terrain streaming where the engine counter was blind.
- Keep the engine generation counter as a secondary guard at threshold 1 because it did become
  visible during real world generation.
- Preserve the configured rate, fractional no-catch-up allowance, capture brake and safety rule.
- Do not change probe scheduling from one noisy A/B pair.
- Treat elapsed game time and singleplayer/multiplayer population as scheduling hints only. The
  terrain-change plan's clean marker and fingerprints must decide whether work is correct to skip.
- Keep the original terrain-change plan intact. The appended ordering, pressure-gate and frontier
  test suggestions are Codex recommendations, not accepted owner decisions.

## Traps

- `CurrentGeneratingChunkCount` can be zero at every scheduler sample while a player is waiting on
  ordinary saved-column priority loads. Testing only world generation makes the counter look more
  complete than it is.
- A route centerline that exists in the save does not prove a generated-only test. View distance can
  generate a halo past the endpoint; compare save rows and coordinates before and after.
- Average FPS hid the established load-phase difference. Tail frame time and 1% low were the useful
  player-facing measures.
- A long yield must not accumulate an unbounded allowance. Otherwise the scheduler gives the player
  a quiet interval and then pays it back as a burst.

## Flagged and unverified

Judgement calls awaiting human review:

- The recommendations appended to `PLAN_TERRAIN_CHANGE_DETECTION.md`, including implementation
  order, storage-pressure gates and applying the activity policy to audits, await independent AI and
  owner review.
- The five-second quiet window is supported by this route, but remains a tuning choice rather than
  a universal optimum.

Claims that still lack the required evidence:

- 0.4.111 has not received the owner's human play verdict.
- Sustained virgin-terrain travel has not been isolated. Run a frozen frontier route comparing
  sweep off, original ungated scheduling and 0.4.111 before declaring scheduling complete.
- The probe-phase tail difference is suggestive, not established.
- Long-run dedicated multiplayer fairness, quiet-server progress and snow-campaign interaction have
  not been measured.
- The terrain-change plan remains unimplemented; its fingerprint stability, read-only database
  behavior, clean marker and section-version path still require their own acceptance matrix.
