# Plan - Terrain change detection: savegame fingerprints and section versions

**Status:** **A1, A2, A3 and A5 are built (0.4.113–0.4.116, Session 103). A4 was not built and is
recommended against as specified. Phase B (B1–B3) is built (0.4.126–0.4.127, Session 109) and
integration-tested in a dedicated sandbox; it has no play verdict yet.** Written 2026-09-18 against
`2fbf2d1` (`0.4.110`); its required scheduling A/B was completed on 2026-09-19 in 0.4.111, and the
sustained virgin-frontier follow-up on 2026-09-19 produced 0.4.112's rate floor.

> **How Phase B was built, and where it differs from §4.2** (Session 109; the wire and storage
> details are in `dev/WIRE_HISTORY.md`, the invariant in `dev/ARCHITECTURE.md` decision 52):
>
> - **B1.** One version per checkpoint transaction, not per row, committed with the counter in
>   `Meta`. The generation is minted when the cache is first versioned and again after a format purge
>   or a write by a build without versions, which an insert trigger detects. A dedicated column
>   rather than SQLite's rowid, which is reused after the highest row is deleted.
> - **B2 records more than one number.** Besides its generation and version, the client keeps the
>   held sections announced but not yet merged and saved. Without them, a span interrupted by a quit,
>   or refused because the section was beyond the serving radius (default 8,192 blocks), would have to
>   be announced again and re-fetched whole on every join. With them, the version can move on at once
>   and the debts resume.
> - **B3.** The server no longer keeps a separate record of refreshed keys: live refreshes come from
>   the same version queries as joins. The `Refresh` flag stays set on the wire because §4.2.4
>   requires an older client to keep behaving as it does today.
> - **No record** (first join after the update, or a rebuilt cache): the owner chose on 2026-09-22
>   that the client re-checks every section it holds, newest winning, over keeping what it has, which
>   was the recommendation. The re-check is resumable, so it is paid once.
> - **Not in the plan:** owed sections are fetched nearest first inside the serving radius, and
>   integrated play does not take part, because the sibling cache's rowids already say what changed.
> - **Acceptance** (§7): tests 8, 10 and 11 pass at harness level. In the sandbox, a rejoin was told
>   exactly the 16 sections changed while away (test 8), and a simulated older-build write forced a
>   full re-check (test 10's path). Test 9 cannot be staged literally, because the server always
>   saves once more at shutdown; its stricter form passed: a rejoin announced exactly the 19 rows of
>   that final save. No second player has been involved.

> **§4.1.5's fast path rests on a hook that does not exist.** Decompiling the installed engine on
> 2026-09-20 showed `ServerMain.EnterRunPhase` returns immediately for `EnumServerRunPhase.Exit`
> without notifying any system or mod, and the game database is disposed before that point. Combined
> with `GameWorldSave` firing *before* the write (§4.1.4, G231), there is no moment at which a mod
> can record the savegame's state after the final save (G236). A clean-shutdown marker as specified
> cannot be written.
>
> Its payoff also fell. §4.1.5 was designed when a world start cost 5m35s of paced engine probes;
> A3 removed that pass, and the whole index measures **73.3 s** on the owner's 3.22 GB world through
> this plan's own code. The fast path would therefore trade an unverifiable assumption for 73
> seconds of low-priority background reading that does not compete with the player.
>
> If A4 is revived, the only sound shape without a post-save hook is: record at `GameWorldSave`
> which columns the imminent write will touch, treat exactly those as unverified, and keep the rest.
> That still trusts that the mod saw every terrain change, which is what §4.3's audit exists to
> recover from — so A5 belongs before it, not after, reversing §11.5's suggested order.

**Required reading first:** [the savegame-sweep scheduling
analysis](ANALYSIS_SAVEGAME_SWEEP_SCHEDULING.md), then `dev/GOTCHAS.md` G190, G192, G199, G227-G238.
This plan answers the analysis's §13 candidate "skip columns whose current terrain revision is
already durable" and its §16 open question "can the mod prove a durable LOD row is current without
introducing a terrain revision journal?" The answer is yes, and this is how.

> The analysis is measurement and options; it explicitly approves no implementation. This document
> is the implementation plan the owner directed on 2026-09-18 after that investigation.
>
> **The analysis's A/B still comes first, and this plan defers to it.** Not because the waste in
> §2 is in doubt - it is measured and exists whether or not the sweep delays the player - but for a
> reason this plan originally missed: the A/B measures today's sweep against no sweep, and once
> Phase A skips unchanged columns the "sweep on" condition no longer does the same work. Land Phase
> A first and the original complaint becomes permanently unmeasurable. The owner assigned that run
> to a parallel effort on 2026-09-19; its harness is `bench/configs/sweep-ab-{on,off}.json` and
> `bench/routes/sweep-active-exploration.txt`.
>
> **2026-09-19 prerequisite result:** the A/B required by the preceding paragraph has now been run.
> Load-phase average FPS was unchanged, but the original sweep worsened the 1% low from 110.3 to
> 88.3 FPS and the 1% frame-time tail from 9.07 to 11.32 ms. Version 0.4.111 added a player-aware
> load gate and recovered 109.0 FPS / 9.17 ms. See the analysis §18 and
> [Session 102](../sessions/SESSION_102.md). The original phases and acceptance criteria below have
> not been rewritten around that result.

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

An earlier revision of this file listed "the A/B is no longer a gate on starting" among these
decisions. The owner did not make that decision; it was this document's own inference and is
withdrawn. Sequencing is as stated in the header: the A/B runs first, on the current build.

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

It does need them to stay in step with the file. Hook `GameWorldSave` to learn *which* columns are
about to be written, then re-fingerprint those few on the private reader thread **after** the engine
has written them. Saves are small, so this is cheap, and it is what makes both §4.1.5 and §4.3
trustworthy.

> **Corrected 2026-09-19 (review of §11).** This section originally said "the engine has just
> written only the dirty chunks, so read those few columns back". That is backwards.
> `VintagestoryAPI.xml` documents `IServerEventAPI.GameWorldSave` as "Triggered **before** the game
> world data is being saved to disk", and the mod's own `LodServerCaptureSystem.OnGameWorldSave`
> already relies on that ordering to flush its data *into* the same save. Fingerprinting inside the
> event therefore records pre-save bytes.
>
> On its own that is merely useless rather than unsafe: a stale fingerprint mismatches the file at
> the next scan and causes a reload, which is the safe direction. It becomes unsafe only in
> combination with §4.1.5, which skips the comparison entirely — one save's worth of changes would
> then never be discovered at startup. Hence the deferred re-fingerprint above.
>
> There is no post-save event in the API. `EnumServerRunPhase.Exit` ("shutdown complete") is the
> only candidate for a final pass and its ordering against the database close is **unverified**;
> do not build on it without checking. The reader thread already tolerates `SQLITE_BUSY` (§4.1.1),
> so deferring the read is cheaper than finding a new hook.

#### 4.1.5 Skipping the scan entirely

The common case must cost nothing. At clean shutdown, record proof of the savegame's current
contents alongside a marker that this session watched the world continuously to that point. At the
next start, if that proof still matches, nothing was written since — skip the scan outright.

**The proof is SQLite's own file change counter**, a four-byte big-endian value at offset 24 of the
database header that increments on every transaction commit and persists in the file. Read it
directly; no connection is needed. Offset 92 holds the version-valid-for number, which equals the
change counter when the counter is current. Measured on the owner's world 2026-09-19:
`bodanboys.vcdbs` reports change counter 651, version-valid-for 651, and read/write format 1/1 —
a rollback journal, consistent with the `journal_mode = delete` recorded in §4.1.1.

**Under WAL the counter is not enough.** WAL defers the main-file header update until a checkpoint,
so a WAL world must also consider the `-wal` side file. §4.1.1 already requires journal-mode
detection, so this is a branch on a decision the design makes anyway. (Orphaned `-wal`/`-shm` files
from an earlier world exist in the owner's `Saves` folder, so this branch is not hypothetical.)

File size and modification time were the original proposal here and are strictly weaker: size is
unchanged by most edits, and a modification time is a filesystem artefact rather than a statement
about the database's contents.

A full scan is then needed only when: the world is adopted for the first time; the proof changed
while the mod was absent or disabled; or the previous session did not shut down cleanly.

**This fast path is only ever as good as the live change hooks.** Skipping the scan means trusting
that everything which changed during the previous session was captured while it was happening
(§4.1.4's first paragraph), not that the fingerprints proved it. §4.3's audit is what covers the
cases the hooks miss, which is why it is part of the design rather than a refinement of it.

#### 4.1.6 What the sweep does instead

The two passes remain, and the safety rule is untouched — a column is still only loaded when its
whole 9x9 neighbourhood exists, because loading a frontier column makes the engine generate its
missing neighbours. The change is what feeds them:

1. Existence and fingerprints come from the private read path, not from 70,225 paced engine probes.
2. A candidate column is loaded when it is safe **and** its fingerprint differs from the stored one
   or is unknown. Otherwise it is skipped.
3. **A fingerprint is committed only when that column's capture has durably succeeded**, in the
   same completion path that marks the column durable — never at the moment the savegame row was
   read. The distinction is the difference between "we have looked at this column" and "we hold a
   correct capture of it", and only the second may cause a skip. Committing at read time creates a
   silent permanent hole: a column whose capture then fails — §4.3 cites three that unloaded before
   the capture queue reached them — carries a fingerprint that still matches an unchanged file
   forever, so neither the next sweep nor the §4.3 audit ever has a mismatch to act on. Added
   2026-09-19 by the review in §12; the original text did not say when a fingerprint became stored,
   and both readings were available.
4. **Skipped columns are still registered as snow-refresh candidates.** `StepLoad` currently calls
   `snowRefresh.Add` on both the frontier and load arms. If the skip path omits it, dormant-terrain
   snow maintenance silently stops reaching everything already captured — no error, just no snow in
   the distance. This is the single easiest regression to introduce here.
5. The absence verifier still runs. Reading the savegame directly cannot generate terrain, so the
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

**What this audit cannot see**, and why §4.1.6 item 3 exists: the audit detects *savegame* changes
the mod missed. It compares the file against the stored fingerprint, so a column whose fingerprint
was stored even though its capture failed presents no mismatch and is never queued. The audit
covers a stale cache row; only committing the fingerprint on capture success covers a missing one.

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
| 14 | Fail a column's capture (unload before capture, or an injected capture error), then restart with the file unchanged | The column is loaded again. A fingerprint must not have been committed for a capture that did not succeed (§4.1.6 item 3) |
| 15 | Clean shutdown immediately after a save that changed terrain, then restart | The fast path may skip the scan only if the changed columns were already captured live; the stored fingerprints must match the file, not the pre-save bytes (§4.1.4) |
| 16 | Restart after a change made while the mod was absent, on a WAL world | The change is detected. The main-file change counter alone is insufficient under WAL (§4.1.5) |

Tests 1, 6, 7, 9, 10 and 14 are the ones that fail quietly if the design is wrong. None of this is
established until the owner has run it; source and harness evidence do not substitute.

Tests 14-16 were added 2026-09-19 by the review in §12.

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
- **Resolved prerequisite (originally “assigned and unrun”):** the savegame-sweep A/B was completed
  on 2026-09-19 before Phase A landed. It established load-phase tail contention and produced the
  0.4.111 player-aware scheduler. The repeated route was mostly saved terrain but generated 178 new
  map columns near its endpoint; a sustained virgin-frontier comparison remains open as scheduling
  evidence, not as a prerequisite for A1/A2.

## 11. Codex recommendations after 0.4.111 — awaiting independent review

> **Review status: reviewed 2026-09-19; see [§12](#12-independent-review-of-11-2026-09-19).** This
> section is a Codex recommendation added after the original plan. It is not an owner decision and
> does not replace or silently amend §§0–10. Its text is retained verbatim below; §12 records what
> holds, what does not, and the edits the review already made to §§4 and 7.

### 11.1 Keep “is work needed?” separate from “when may work run?”

Retain 0.4.111's player-activity gate for first adoption and every column whose fingerprint really
changed. Phase A should remove unnecessary work; the scheduler should make necessary work polite.
Neither mechanism should substitute for the other.

The proposed no-work decision order is:

1. If the previous session closed cleanly, the mod watched changes continuously, and the savegame
   file's size and modification time still match, skip the scan entirely as §4.1.5 specifies.
2. If that cheap proof is unavailable, read the save privately and compare per-column fingerprints.
3. Priority-load only missing fingerprints and actual mismatches, retaining snow-candidate
   registration and the complete-neighbourhood rule.
4. Apply the same player-activity, engine-generation and no-catch-up scheduling policy to those
   remaining loads.

### 11.2 Do not use elapsed game time or player count as invalidation proof

Elapsed time is not evidence of a terrain change. Hours can pass without a relevant column changing,
while a player, command, simulation step or mod can change one immediately. It is useful only for
pacing, such as the minimum age before a background audit revisits a clean fingerprint.

Singleplayer versus multiplayer is likewise a workload hint, not a truth test. A single local
player can modify terrain. Many remote players need time to do work, but while Vintage Horizons is
active its loaded-change hooks should record those changes directly. If the mod was disabled, the
server crashed, or a different tool changed the save, fingerprints must discover the difference.
Do not run a full sweep merely because a multiplayer world had many players, and do not skip one
merely because a world was singleplayer.

### 11.3 Add storage pressure only after the frontier measurement

The controlled run establishes that player movement is a useful primary gate. It does not yet tell
whether dirty-section count, oldest dirty age or storage backlog should also suppress loads. The
recommended frontier A/B should compare:

- sweep disabled;
- the pre-0.4.111 ungated scheduler;
- 0.4.111's player-aware scheduler.

Restore the same frozen profile for each run, start near the generated endpoint around chunk X
8050, and continue through untouched terrain. Record per-waypoint readiness, generation-queue
history, frame and server-tick tails, new save rows, sweep hold reasons, effective rate, capture
backlog, dirty count/age and save backlog. Add storage-pressure gates only when those measurements
show that downstream persistence remains a player-facing source of contention.

### 11.4 Preserve idle progress and fairness

- A no-player server should use the configured sweep rate, subject to capture and storage safety.
- Any moving player should extend the quiet window for everyone; do not let one player's sweep work
  compete with another player's terrain demand.
- A stationary player should permit progress after the quiet interval.
- Every hold must preserve the capped allowance so resumption cannot burst.
- Background audits and dormant-snow loads should eventually adopt the same fairness policy, while
  retaining separate low rates and eventual-convergence tests.
- `CurrentGeneratingChunkCount` should remain a secondary actual-generation guard. The A/B proved
  it is blind to ordinary saved-column priority demand at scheduler cadence.

### 11.5 Suggested implementation order

1. Preserve the 0.4.111 scheduler and capture its sustained-frontier baseline before later work
   changes the workload.
2. Phase B may proceed independently because it fixes stale rejoin data and shares no Phase A code.
3. Build A1 and A2: the private read-only path, schema guards, existence map and fingerprints.
4. Build A3 on top of the existing scheduler: unchanged-column skip with snow and frontier safety
   preserved.
5. Add A4's live maintenance and clean-shutdown fast path.
6. Add A5 auditing last, after its false-positive rate and fair scheduling are measurable.

No probe-phase change is recommended from the current evidence. Its one on/off pair showed a worse
tail with probes enabled but conflicting average/median movement. Revisit it only if repeated runs
or waypoint readiness show a material probe-specific penalty.

## 12. Independent review of §11 (2026-09-19)

Requested by the owner on 2026-09-19 as the follow-up assigned in `STATUS.md` §Next work item 2.
Reviewed against §§0-10, [the sweep analysis](ANALYSIS_SAVEGAME_SWEEP_SCHEDULING.md), the 0.4.111
source at `b2fe181`, and the installed `VintagestoryAPI.xml`. The owner accepted the review's
recommendations on 2026-09-19 without itemising them; the corrections marked "review of §11" in
§§4.1.4, 4.1.5, 4.1.6, 4.3 and 7 were made under that acceptance.

**Verdict.** The five recommendations are directionally right and none is dangerous. Two rest on
premises that are not true - one API behaviour and one description of the current build - and the
section leaves open a silent-failure hole it inherits from §§4.1-4.3 without noticing. §11 is
retained as a proposal, not promoted, and the corrections below were applied to the plan proper.

### 12.1 What holds

- **§11.1's core separation.** Phase A removes unnecessary work; the scheduler makes necessary work
  polite. Neither substitutes for the other. This is the right headline for the section.
- **§11.5 item 1.** Capture the sustained-frontier baseline before Phase A changes what "sweep on"
  means. This is the same argument the header makes for the original A/B and is correct.
- **§11.4's capped allowance.** Already true by construction, not an outstanding requirement:
  `LodTickAllowance.Available` caps each grant at one tick's rounded share and drops older credit
  when a tick arrives late, and `Spend` deducts only actual use. A five-second hold cannot release
  a burst.

### 12.2 Findings

1. **§11.1 step 1 rested on a wrong API assumption.** `GameWorldSave` fires *before* the engine
   writes. Corrected in §4.1.4 with the citation; the fast path's consequence is recorded there.
2. **Nobody said when a fingerprint becomes "stored",** and the two available readings differ by a
   permanent silent hole. Resolved in §4.1.6 item 3, with the audit's matching blind spot in §4.3
   and acceptance test 14.
3. **§11.1 step 1's size-and-mtime proof is weaker than SQLite's own.** Replaced in §4.1.5 by the
   header change counter, measured on the owner's world, with the WAL branch stated.
4. **§11.3 mis-states the current build.** It reads as though no pressure gating exists. Capture
   backlog has gated loads since 0.4.68 - `LodSavegameSweep.MaxCaptureBacklogForLoads` = 32,
   applied in `LoadsAllowed`. What is absent is *storage* pressure, and those signals already exist
   too: `LodPipeline.DirtySections`, `LodPipeline.OldestDirtyAgeMs`,
   `LodServerConfig.WorldGenDirtySectionLimit`, and `AutomaticGenerationGate`, which the worldgen
   path already consults. Analysis §13 lists reusing that gate as a candidate. **The recommendation
   should read "reuse the existing gate after the measurement", not "add storage pressure"** - as
   written it invites a second capture brake beside the one already there.
5. **§11.4 has no progress floor.** "Any moving player should extend the quiet window for everyone"
   means a populated server can hold the startup load pass indefinitely; Session 102 logged 1,621
   holds on a single-player route. Analysis §15 already requires that "multiplayer demand cannot
   starve or be starved by the sweep", and §11.4 satisfies only half of it. The cost is not
   obvious: snow-refresh candidates are registered from *inside* `StepLoad`, so a starved sweep
   silently starves dormant-terrain snow maintenance as well. **Sweep completion time and effective
   rate must be acceptance numbers in the frontier A/B, not merely recorded** - otherwise "polite"
   is satisfied by never running.
6. **"Background audits and dormant-snow loads should eventually adopt the same fairness policy" is
   too weak.** `LodSavegameSweep.StepRefresh` issues `LoadChunkColumnPriority` into the same fast
   lane with no player gate, no engine gate and no capture-backlog brake. It is 1/s by default
   against the sweep's 16/s, so the magnitude is small - but the startup pass terminates and snow
   campaigns do not, and this arm was outside the measured condition entirely. It should be named
   as the known-open lane.
7. **Minor: §11.5's closing probe paragraph contradicts the plan it is appended to.** Phase A2/A3
   removes the 70,225-probe pass outright (§4.1.6 item 1). The paragraph should be scoped to
   *pacing* changes to the existing path, which is what it means.
8. **Minor: §11.2 restates analysis §18.5 almost verbatim.** By the plan's own rule in §1.2 - two
   mechanisms describing the same fact is how they come to disagree - it should be a pointer.

### 12.3 Aside from reading the 0.4.111 gate

`LodPlayerTerrainActivity.players` never evicts departed players. It is bounded by unique player
UIDs per session, so the size is immaterial, and a rejoin after a long absence is still correctly
treated as activity - but only because the stale entry fails the `Sample == sample - 1` check
rather than because it was removed. Worth a comment if anything else ever reads that dictionary.

### 12.4 Not changed

§11's text stands as written; this review does not edit it. Findings 4-8 are recommendations about
§11 itself and are for the owner to fold in or discard. Findings 1-3 were factual errors or
genuine gaps in §§4 and 7 and have been corrected there, each marked with its date and evidence.
