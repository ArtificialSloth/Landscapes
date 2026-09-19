# Plan - Terrain change detection: savegame fingerprints and section versions

**Status:** Proposed, not started. Written 2026-09-18 against `2fbf2d1` (`0.4.110`), owner-directed.

**Required reading first:** [the savegame-sweep scheduling
analysis](ANALYSIS_SAVEGAME_SWEEP_SCHEDULING.md), then `dev/GOTCHAS.md` G190, G192, G199, G227-G228.
This plan answers the analysis's §13 candidate "skip columns whose current terrain revision is
already durable" and its §16 open question "can the mod prove a durable LOD row is current without
introducing a terrain revision journal?" The answer is yes, and this is how.

> The analysis is measurement and options; it explicitly approves no implementation. This document
> is the implementation plan the owner directed on 2026-09-18 after that investigation. The A/B it
> demands is still worth running, but no longer gates this work: the waste documented in §2 exists
> whether or not the sweep measurably delays player terrain.

## 0. In one paragraph

Vintage Horizons has two places where it cannot tell whether data it holds is still current, and it
resolves both by redoing work. The server cannot tell which savegame columns changed since it last
looked, so the startup sweep re-examines all of them on every world start. The client cannot tell
whether a cached section changed while it was offline, so it silently keeps a stale copy forever.
Both are solved by making "has this changed?" cheap to answer, with two different tools: a content
fingerprint where no ordering exists (the savegame, which stamps nothing), and a monotonic version
where the mod controls the store (its own cache). Neither changes what the mod draws; both change
how much work it repeats.

## 1. Goal and decisions

### 1.1 Goals

1. A world start that finds nothing changed does approximately no work.
2. A world start that finds changes examines and loads exactly those columns.
3. A returning client receives every section that changed while it was away, and no others.
4. Neither guarantee depends on trusting a cache row as savegame truth.

### 1.2 Owner decisions taken 2026-09-18

- Both halves are in scope: server-side fingerprints and client-facing section versions.
- **Section versions replace the refresh manifest.** `AssistKeyManifest.Refresh` is subsumed, not
  kept alongside. Two mechanisms describing the same fact is how they come to disagree.
- Losing cache data is acceptable during development. This plan still avoids a schema wipe where a
  wipe would be gratuitous, for the reason in §4.1.3.
- The savegame sweep A/B remains worth running and is no longer a gate on starting.

### 1.3 Non-goals

- Making the load pass itself faster. Fingerprints make loading **rarer**, not quicker; a column
  that genuinely changed costs exactly what it costs today.
- Changing what terrain is captured, how it is downsampled, or how it is drawn.
- Changing the sweep's spawn-centred square, its radius, its safety rule, or its absence verifier.
- Changing the engine's savegame in any way. See §4.2.4.

## 2. The waste this removes, measured

All figures are from the owner's machine on 2026-09-18, against the real `bodanboys.vcdbs`
(3.22 GB, 772,480 chunk rows, 64,179 map-chunk columns), with the game closed.

| Operation | Scope | Time | Rate |
|---|---|---:|---:|
| Current probe pass (observed 0.4.108 session) | 70,225 positions, 4 km radius | 5m 35s | ~210/s |
| Random point lookups, direct | 20,000 positions | 1.43 s | 13,990/s |
| Sequential scan of `mapchunk`, direct | 64,179 columns, whole world | 5.1 s | 12,536/s |
| Sequential read + BLAKE2b of `chunk`, direct | 772,480 chunks, whole world, 1.57 GB | 23.3 s | 33,132/s |

Two conclusions follow, and the second corrects a wrong inference made earlier in the same session:

1. **The probe pass is not slow because of its access pattern.** Random point lookups measured
   13,990/s against a sequential scan's 12,536/s — the same speed. Any claim that random access is
   the problem is wrong.
2. **It is slow because it is paced to ~320/s** (`MaxProbeIssuesPerTick` 16 per 50 ms callback),
   and that pacing is deliberate: the lookups are served by the engine's chunk thread, the same
   thread that loads terrain for the player, and the code comment records that the cap was made
   *smaller* to stop main-thread publication bursts. The engine's synchronous variant,
   `BlockingTestMapChunkExists`, is documented as usable only before `EnumServerRunPhase.RunGame`.
   So going faster is not a constant change; it requires leaving the engine's thread.

Separately, and independent of speed: **the load pass never asks whether the cache already holds a
column.** It consults only `LodColumnMap.Classify` — does it exist in the savegame, and is its 9x9
neighbourhood complete. On a world already fully captured, every start repeats the entire job. The
owner's 2026-09-17 log shows thousands of cache sections rewritten during a sweep of such a world.

`LodPipeline.IsAuthoritativeChunkDurable` already exists and is used by `LodWorldGenJob` for exactly
this kind of skip. The sweep does not use it, and must not: holding a column is not evidence that
the column is current. That distinction is the whole reason this plan needs fingerprints rather than
an existence check.

## 3. Why two different mechanisms

| | Server ↔ savegame | Server ↔ client |
|---|---|---|
| Who controls the store | Nobody the mod can influence | The mod |
| Ordering available | None | Can be created |
| Tool | Content fingerprint | Monotonic version |
| Cost to ask "what changed?" | Read and hash the bytes | Send one integer |

**The rule:** use a version when one side is strictly newer and the store can be stamped; use a
fingerprint only when no ordering can exist. Applying fingerprints to the client boundary would
mean shipping ~64,000 fingerprints (about 512 KB) and diffing them on every join, to replace a
scheme that needs one 8-byte number. Applying versions to the savegame is impossible: `chunk` is
`(position INTEGER PRIMARY KEY, data BLOB)`, and in SQLite `INTEGER PRIMARY KEY` *is* the row id, so
re-saving a chunk reuses its number. Verified on the owner's world: 772,480 rows, zero where row id
differs from position. There is no timestamp, counter or version anywhere in that row.

**Why a fingerprint is sound here.** `ServerSystemLoadAndSaveGame` writes only chunks whose
`DirtyForSaving` flag is set. An untouched chunk is never rewritten, so its stored bytes stay
byte-identical and its fingerprint is stable. A fingerprint can therefore only produce a false
*positive* — reporting change when volatile state (light, snow, block entities) moved but terrain
did not. That costs an unnecessary load, never a wrong result.

## 4. Design

### 4.1 Phase A - server-side fingerprints

#### 4.1.1 The read path

The mod opens the savegame file **a second time, read-only, on its own thread.** It does not use the
engine's database object.

Rejected alternative: reflection onto `ChunkServerThread.gameDatabase`, which is `internal`. It
works and the mod has precedent for reflection, but the engine's reads are not inside
`lock (transactionLock)` while its writes are, so every read would have to be serialised onto the
engine's chunk thread — leaving the work competing with player terrain, which is the entire
complaint. A private connection removes the competition instead of making it cheaper. It also
cannot break on a game update that renames a private field.

Requirements on that connection:

- Read-only, opened with SQLite's read-only URI flag. It must be incapable of writing.
- Its own thread. Never the main thread, never the chunk thread.
- **Detect the journal mode; never set it.** The owner's world measured `journal_mode = delete`,
  which means a reader and the engine's writer cannot overlap. Under WAL they can, and the batch
  pacing below may relax. Switching a world to WAL is a permanent change to the player's file that
  the engine then inherits, creates side files, and behaves badly on network drives: the mod reads
  this setting and adapts, and under no circumstance changes it.
- Short batches with yields, sized so the engine's autosave never waits long. Saves measured about
  five minutes apart and wrote nine chunks in one observed case; a batch in the low tens of
  milliseconds makes contention immeasurable.
- Every read honours a busy timeout and retries. `SQLITE_BUSY` is expected, not exceptional.
- If the file cannot be opened at all, the feature disables itself for the session with one named
  log line and the sweep behaves exactly as it does today. It is an accelerator, never a
  prerequisite.

#### 4.1.2 What is fingerprinted

One fingerprint per **chunk column**, not per chunk. The sweep's unit of work is the column, and any
change anywhere in a column forces the whole column to reload regardless.

A column's fingerprint covers its chunk rows in ascending `y` order, plus the count of rows present,
so that a column which gained or lost a layer differs even if the surviving layers did not change.
Sixty-four bits is ample; this is change detection, not security.

The existence map the safety rule needs comes from the same pass. `mapchunk` gives one row per
column and answers the same question `TestMapChunkExists` answers today.

#### 4.1.3 Where fingerprints are stored

A **new table in the existing cache database**, created with `CREATE TABLE IF NOT EXISTS`, keyed by
chunk column. Not a column on `Section`, and not a schema-version bump.

Not because the data is precious — the owner has said it is not — but because `Section` is keyed by
`(Detail, SX, SZ)` and this is keyed by chunk column. They are different key spaces at different
granularities, and putting one on the other is mismodelling. A bump would also wipe the cache, and
the cache is the baseline any measurement of this feature has to be made against: starting every
test from an empty cache means every run is the cold worst case and the improvement is invisible.

An old cache gains the table empty on first open. "No fingerprint" reads as "assume changed", which
is exactly today's behaviour, and the table fills as the sweep runs. There is no migration step and
nothing to undo.

**A version bump would be required** if the fingerprint ever became authoritative — a `Section` row
marked "proven current, never re-examine". Existing rows carry no such proof. Keep the fingerprint
advisory: it may cause work to be **skipped**, never cause a claim about content.

#### 4.1.4 Keeping fingerprints current during play

Fingerprints describe *saved* bytes, so they can never be fresher than the last save. During play
the mod captures from chunks in memory and already learns of every change through `ChunkDirty`,
`DidBreakBlock` and `DidPlaceBlock`; it does not need fingerprints to know what changed.

It does need them to stay in step with the file. Hook `GameWorldSave`: the engine has just written
only the dirty chunks, so read those few columns back and re-fingerprint them. Saves are small, so
this is cheap, and it is what makes both §4.1.5 and §4.3 trustworthy.

#### 4.1.5 Skipping the scan entirely

The common case must cost nothing. At clean shutdown, record the savegame file's size and
modification time alongside a marker that this session watched the world continuously to that point.
At the next start, if the file is unchanged, nothing was written since — skip the scan outright.

A full scan is then needed only when: the world is adopted for the first time; the file changed
while the mod was absent or disabled; or the previous session did not shut down cleanly.

#### 4.1.6 What the sweep does instead

The two passes remain, and the safety rule is untouched — a column is still only loaded when its
whole 9x9 neighbourhood exists, because loading a frontier column makes the engine generate its
missing neighbours. The change is what feeds them:

1. Existence and fingerprints come from the private read path, not from 70,225 paced engine probes.
2. A candidate column is loaded when it is safe **and** its fingerprint differs from the stored one
   or is unknown. Otherwise it is skipped.
3. **Skipped columns are still registered as snow-refresh candidates.** `StepLoad` currently calls
   `snowRefresh.Add` on both the frontier and load arms. If the skip path omits it, dormant-terrain
   snow maintenance silently stops reaching everything already captured — no error, just no snow in
   the distance. This is the single easiest regression to introduce here.
4. The absence verifier still runs. Reading the savegame directly cannot generate terrain, so the
   promise it measures becomes easier to keep, not harder.

### 4.2 Phase B - section versions

#### 4.2.1 The defect this fixes

Traced in source on 2026-09-18, not yet reproduced in game. On join the server sends its full key
manifest; `LodRemoteKeySet` registers only keys with no local data, and `AssistSectionRequest`
asks only for those. `AssistSection` carries a key and a blob and **no version of any kind**. The
refresh path (`AssistKeyManifest.Refresh`) forces a re-fetch of keys the client already holds, but
it is sent only to currently-greeted online players, and `TakePersistedLevelZeroChanges` is drained
whether or not anyone is listening.

So a section that changes while a player is offline is never offered to them again. They keep the
stale copy. The server comment asserting that "a later greeting receives the current rows through
the ordinary full manifest" holds only for a client with no local cache — and the client cache is
the reason rejoin is instant, so the normal case is the uncovered one.

#### 4.2.2 The mechanism

Every `Section` row carries a version that increases whenever its durable contents change. The
client records, per world, the highest version it has accepted. On join it sends that one number;
the server streams every section above it, nearest first, through the existing bounded request path.

This subsumes the refresh manifest. `AssistKeyManifest.Refresh` is removed rather than kept beside
it, per the owner's decision: a refresh is simply "your version is behind", and one rule cannot
disagree with itself.

#### 4.2.3 Generation identity, and why it is not optional

A version scheme fails silently the moment versions go backwards. They go backwards when the server
cache is rebuilt, wiped by a schema change, or restored from a backup — and the owner rebuilds
caches routinely during development, so this is the expected path, not the edge case. A client
holding "highest seen 5,000" would then ignore everything a freshly rebuilt server offers, forever,
with no error.

The section transport has no world or session identity at all; only the silhouette messages carry
`SessionId`. So versions must travel with a **generation stamp**, minted when a cache is created and
re-minted whenever it is wiped or rebuilt. A client whose recorded generation does not match resets
its highest-seen to zero and refetches from scratch. The silhouette transport's session identity is
the precedent to follow; `LodLocalOfferSource`'s periodic full scan, which exists to catch reused
SQLite row ids, is the same defensive idea one layer down.

#### 4.2.4 Compatibility

Message changes are append-only, as every previous transport change in this project has been. An
older client ignores the version field and behaves exactly as it does today, including keeping stale
sections. A newer client against an older server receives no version and falls back to present
behaviour. `dev/WIRE_HISTORY.md` records the protocol change; the assist protocol number advances
only if an existing field's meaning changes, which this does not.

### 4.3 Audits

Continuous watch is not airtight. The owner's 2026-09-17 log shows three columns that unloaded
before the capture queue reached them; crashes and capture errors do the same. These are silent —
the cache simply holds terrain that is no longer true.

A background trickle re-reads and re-fingerprints a rotating slice of the world on the private read
path and compares against the stored fingerprints, queueing any mismatch for recapture. Auditing the
owner's entire world costs 23 seconds of reading; spread over an hour it is immeasurable. Rate is a
server setting, and zero disables it.

An audit can only compare against something stored earlier, which is why §4.1.4's save-hook
fingerprinting is a prerequisite rather than an optimisation. Without it there is nothing to audit
against, and verification would mean rebuilding each section to compare — which costs the same as
recapturing it, and buys nothing.

## 5. What this does not improve

Stated so the plan is not oversold:

- **First adoption is unchanged.** Installing the mod on a large existing world still pays one full
  load pass. Every start after that is cheap. The win is repetition, not first contact.
- **Changed columns cost what they cost.** The 7-14 columns/s load path is untouched.
- **Vanilla servers gain nothing.** The client works against servers without the mod, where there is
  no server cache, no fingerprints and no versions; behaviour there is unchanged.
- **Fingerprinting is strictly a server-half feature.** The savegame exists only on the server. No
  part of this runs on a client, and a client must never be given a savegame read path.
- **The sweep's coverage is unchanged.** Same square, same radius, same safety rule, same uncaptured
  frontier border.

## 6. Phases

Each phase is independently useful and independently shippable.

| Phase | Delivers | Depends on |
|---|---|---|
| A1 | Private read-only savegame connection, journal-mode detection, batch pacing, busy handling, self-disable on failure | - |
| A2 | Column fingerprints and the existence map from that connection; new cache table | A1 |
| A3 | Sweep consumes both: skip unchanged, keep snow registration, keep the safety rule | A2 |
| A4 | Save-hook fingerprint maintenance; clean-shutdown marker; skip-the-scan fast path | A2 |
| A5 | Background audit at a configurable rate | A4 |
| B1 | Section version column and generation stamp; server mints and advances them | - |
| B2 | Append-only transport fields; client records highest-seen per world and generation | B1 |
| B3 | Refresh manifest removed; versions serve both online and rejoin cases | B2 |

A and B share no code and may be done in either order. B is the smaller job and fixes a defect
rather than an inefficiency, which is an argument for doing it first.

## 7. Acceptance

The positive tests are obvious and insufficient on their own. The negative tests are what
distinguish "correctly fast" from "silently skipping work it should do", and that failure is
invisible until someone notices distant terrain is wrong.

| # | Test | Expected |
|---|---|---|
| 1 | Restart a fully-captured world with nothing changed | **Zero columns loaded.** Startup cost is the file-timestamp check |
| 2 | Edit terrain with the mod disabled, restart | Exactly the edited columns are loaded; no others |
| 3 | Edit terrain externally, restart | Same as 2 |
| 4 | First adoption of a world with no fingerprints | Behaves as today; full load pass; fingerprints populate |
| 5 | Crash the server mid-session, restart | No clean marker, so a full scan runs and converges |
| 6 | Snow refresh after a skipped start | Dormant-terrain snow still reaches columns that were skipped |
| 7 | Absence verifier after a skipped start | Still passes; nothing was generated |
| 8 | Rejoin after offline server-side changes | Exactly the changed sections arrive |
| 9 | Rejoin with nothing changed | No sections transferred |
| 10 | Rejoin after the server cache was wiped and rebuilt | Generation mismatch detected; client refetches from zero |
| 11 | Old client against new server, and new client against old server | Both behave as their own version does today |
| 12 | Autosave during a scan | No stall attributable to the mod; saves complete normally |
| 13 | Savegame file unreadable or locked at startup | Named log line; sweep runs as today; no crash |

Tests 1, 6, 7, 9 and 10 are the ones that fail quietly if the design is wrong. None of this is
established until the owner has run it; source and harness evidence do not substitute.

## 8. Risks

| Risk | Handling |
|---|---|
| A second reader stalls autosave | Short batches, busy-timeout retries, journal-mode detection; measure the engine's save duration during a scan |
| Fingerprint churn from volatile state | Accepted: costs an unnecessary load, never a wrong result. Measure the false-positive rate on a real world before tuning |
| Skipping breaks snow maintenance | §4.1.6 item 3; acceptance test 6 |
| Versions go backwards | Generation stamp, §4.2.3; acceptance test 10 |
| Two update mechanisms disagree | Refresh manifest removed outright, not kept beside versions |
| Private read path is a second opinion about the world | Read-only connection, never writes, never changes journal mode, self-disables on any failure |
| Engine changes the savegame schema | The mod reads `chunk` and `mapchunk` by position only. Detect an unexpected schema and self-disable rather than guess |

## 9. Files expected to change

- `VintageHorizons/src/Net/LodSavegameSweep.cs` - consumes existence and fingerprints; skip arm
- `VintageHorizons/src/Storage/` - new read-only savegame reader; fingerprint table on `LodStore`
- `VintageHorizons/src/Net/LodServerCaptureSystem.cs` - save-hook fingerprinting; shutdown marker
- `VintageHorizons/src/Net/LodAssistProtocol.cs` - version and generation fields; `Refresh` removed
- `VintageHorizons/src/Net/LodAssistServerSystem.cs` - version minting; manifest by version
- `VintageHorizons/src/Net/LodAssistClient.cs`, `VintageHorizons/src/Lod/LodRemoteKeySet.cs` -
  highest-seen per world, generation reset
- `VintageHorizons/src/Net/LodServerConfig.cs` - audit rate
- `tests/VintageHorizons.Checks/` - fingerprint stability, skip arm, snow registration, generation
  reset, version ordering
- `dev/WIRE_HISTORY.md` - the append-only transport change, at completion

## 10. Open questions

- What false-positive rate do fingerprints actually have on a played world? Volatile state may make
  more columns look changed than truly are. Measurable before any of this ships, on the owner's
  world, offline.
- Should the audit prefer columns nobody is near, to keep its reads off the same pages the engine is
  using?
- Does the client cache need its own fingerprint story, or is the section version sufficient? The
  current answer is that the version is sufficient, because the server is the only writer.
- The savegame-sweep A/B from the analysis remains unrun. It no longer gates this work, but it is
  the only thing that will say how much of the player-visible complaint this actually removes.
