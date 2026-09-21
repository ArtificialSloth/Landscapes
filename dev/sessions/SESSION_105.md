# Session 105 — What the fingerprint was really reporting, and the two builds that fixed it

**Date:** `2026-09-20`
**Branch/commit:** `codex/water-depth-light`; checkpoints 0.4.118 `0c9ac69`, 0.4.119 `3dd8fb3`; nothing pushed
**Mod version:** `0.4.119`
**Assist protocol / blob / schema:** `1 / 4 / 6`

> Session records are Tier 3 history. Write narrative as needed, but preserve the four required tail sections so future harvesting remains mechanical.

## Context and investigation

The owner chose the third item from Session 104's list: measure the audit's real false-positive
rate, which is the terrain-change plan's §10 open question and had only a sandbox number (33
mismatches in 4,661 re-reads) that STATUS itself said was not a rate for his setup.

Everything here was read offline and read-only. The evidence was four savegames and two caches:
the frozen 2026-08-21 `bodanboys` seed under `.testdata/profiles/sweep-ab/seed`, the same profile's
working copy after one sandbox run, the owner's live `bodanboys.vcdbs` of 2026-09-15, and his
`test2` world with the server cache written by the 0.4.116 session of Session 104. Later in the
session the owner played 0.4.118 and 0.4.119 on `test2` and those logs became evidence too.

Out of scope: any change to what a capture stores, to the sweep's scheduling, or to the silhouette
work. The storage-pressure question settled in Session 104 was not reopened.

## Work narrative

### 1. Two measurements, because "false positive" has two meanings

The plan asks how often a fingerprint moves for a reason the capture cannot see. That is a
statement about *content*. The audit's own mismatch count is a statement about *claims*. They are
different populations and they were measured separately.

**Content churn** came from comparing two settled snapshots of the same world with a new offline
tool: hash every `chunk` row, group rows into columns, and for every column whose bytes differ,
decode the rows' protobuf fields and record which fields moved. The P0 comparator
(`scripts/worldgen-save-compare.py`) already owned a verified protobuf decoder and the chunk
position layout, so it was reused rather than rewritten.

On the owner's real world across 25 days of play and generation (seed 2026-08-21 → live
2026-09-15), of 62,390 columns present in both, **13,754 changed** — 22.05% of the world, but
**43.5% of the 31,583 columns that lie inside his 128-chunk sweep square**, which is the
denominator that matters because all of the change is inside it. Of those changed columns:

| Why the fingerprint moved | columns | share |
|---|---:|---:|
| blocks, fluids or decors | 11,261 | 81.87% |
| non-chisel block entities only | 1,661 | 12.08% |
| entities, light, mod data only | 822 | 5.98% |
| chiselled block contents | 10 | 0.07% |

The block-entity churn is berry bushes, mycelium, transient blocks, tree hollows, locust nests and
beehives ticking. `MicroBlock` and `Chisel` — the only classes a capture reads — appear in 202 and
18 columns.

**Claim staleness** came from replaying the audit's own comparison offline: recompute each stored
`ColumnFingerprint` from the savegame and compare. On the owner's `test2` cache, written during his
0.4.116 session, **162 of 2,323 claims (7.0%)** were already disputed when that session ended. In
the sandbox the same replay gave 76 of 289 (26.3%), higher only because its 8-chunk sweep square
sits entirely inside the player's 12-chunk load radius.

### 2. Why a claim is wrong the moment it is made

The mechanism, traced in source and then supported by three independent measurements: the sweep
fingerprints the savegame once at world start; it then asks the engine to load a column; loading a
dormant column is itself what changes it, because the engine catches up its transient blocks, its
snow and its block entities on the way in; the capture reads the column *after* that, so the cache
is right; the next save writes those engine changes, and the claim — taken from the pre-load
reading — no longer matches the file.

Supporting evidence: claims made in the first 90 s of the sweep were stale at 10.6% against 5.2%
for later ones, which is what a longer claim-to-save window predicts; in the sandbox, columns the
sweep had loaded changed at 33.6% against 0.019% elsewhere; and for 48 of the 162 disputed `test2`
claims the cached section had itself been rewritten after the claim, against 2.2% of the agreeing
claims, so the cache had already absorbed the change the fingerprint was complaining about.

### 3. The review pass, and what it corrected

The owner asked for a review of the measurement before acting on it. It changed four things and
weakened a fifth:

- The 22.05% figure used the whole savegame as its denominator; inside the swept square it is
  43.5%.
- "The disputed claims are spread evenly, not clustered where you stood" was overstated. There is
  no distance gradient, but a nearest-neighbour test puts the disputed set at 1.47 chunks against
  1.83 ± 0.08 for random subsets of the same size, so they are locally clumped.
- The mapchunk-only snow figure came from the first 2,000 columns in sorted order, which is a strip
  of the map. A random sample gives 26%, not 37%.
- The 25-day pair covers play *and* `/vhworldgen` generation, not play alone.
- "The recapture is wasted" was asserted, not measured. It is now measured for 48 of 162 and
  argued for the rest from `ChunkDirty`'s documented semantics ("blocks or light values have been
  modified") plus a capture-drop counter of 0–1 for the whole session. Not proven.

The review also verified the port that produced the numbers: 2,161 of 2,323 stored claims matched
it, `test2` holds exactly 14 chunk layers per column with no rows a keyed read could miss, all its
rows are dimension zero, all 162 disputed columns pass the sweep's 9×9 safety rule, and the sandbox
world is one four-minute run from the frozen seed because the bench restores the save and both
caches unconditionally and the seed cache has no fingerprint table at all.

### 4. The exclusion list, decided with the owner

The owner's reaction to the bush churn was that the fingerprint should ignore what does not change
the landscape. Two lists were agreed explicitly before any code changed, with the point made that
no *blocks* are excluded — only the hidden state stored beside them.

Kept: every block in the column (which covers terrain, ore, caves, player building, snow layers
appearing and melting, and a bush changing to its ripe or empty *form*), fluids, chiselled-block
contents, the layer count and structural markers, and any field a future game version adds.

Dropped: block-entity state except chiselled blocks, entities and their count, block light, light
saturation, light positions, other mods' chunk data, the placed/removed counters, and the
block-entity count. The owner added decors to the dropped list on the grounds that ground gaining
decor is being worked on for other reasons anyway.

The rule is exclusion rather than inclusion so that an unrecognised field still counts as a change.

### 5. 0.4.118, and what it is worth

`LodSavegameScan` now walks a row's top-level protobuf fields and hashes the ones that describe the
landscape, filtering the block-entity list to the classes a capture reads. Those classes are
resolved from the game's own block registry at world start — every registered block names its
entity class, so a mod's own microblock subclass is found the same way vanilla's two are.

Three fallbacks are deliberate: a row that does not parse as that shape is hashed whole, which is
the old behaviour; an entry whose class name cannot be read is kept; and with no registry answer
every block entity is kept.

Measured with the shipping code against the same two real savegames: **13,754 flagged columns
become 11,307**, exactly matching the independent offline prediction, with no column flagged that
the whole-row hash did not already flag. The variant without the chisel filter gives 12,967. About
45% of the bytes are no longer hashed, most of that other mods' chunk data — Watersheds'
groundwater and flow vectors are 84% of the bytes in a sample of his rows.

Claims now carry a rule version and are dropped when it changes. A latent defect was fixed in the
same place: a schema purge deleted sections but left the claims describing them, which would have
made the sweep skip terrain the cache no longer held.

### 6. The owner's 0.4.118 sessions: the skip path, demonstrated

First start read 17,219 columns in 1.2 s, loaded 10,439 over about 25 minutes, skipped 2,112
frontier columns, verified 256/256 absent positions still absent, and recorded no capture errors or
drops. The restart **skipped 10,040 columns, loaded 399, and finished in 37 seconds**. That is
phase A's promise, demonstrated on his machine for the first time.

The 399 reloads are 3.8% of what the first session captured — the stale-at-birth share, down from
the 7.0% measured on 0.4.116 before the landscape-only rule.

### 7. The blind spot that made 0.4.119 necessary

Nine minutes of that first session ran the audit and the revived snow refresh with the log saying
nothing at all about either, because the server printed a status line only while a job was
unfinished and both arms only start when the sweep finishes. Reading the cache showed 336 claims
written during those quiet minutes, but with no line to read they could not be attributed between
audit recaptures and the capture queue draining.

0.4.119 therefore gives the sweep a signature of what its background arms have done and prints the
status line while that moves, and adds the claim clause to it. Separately it moves claim
commitment: taking a durable capture and claiming it are now two steps, with the world-save event
scheduling a re-read a few seconds later and the claim taken from what the file then holds. A
column with a capture queued, or one no longer durable, is left settling rather than claimed.

Three fallbacks again: no read path claims from the startup reading as before; a session ending
with columns still settling claims them the same way, because losing a save interval's proven
captures would be worse than a stale claim; and re-read batches unanswered three times say so once
and turn the refresh off for the session.

### 8. The owner's 0.4.119 session, and the numbers that had never existed

Sweep: 10,172 skipped, 267 loaded, finished in 27 seconds.

Audit, reported for the first time: **10,591 columns re-read at 16/s, 81 no longer matched and were
queued for recapture, one full pass over 10,431 claims** in about twelve minutes. That is 0.76% of
re-reads, close to the sandbox's 0.71%, and it is now a production number rather than an inference.

Snow refresh, also for the first time: 550 recaptures, 81 regional campaigns, 23 dormant-region
probes over 12,551 candidates, with 12,104 still queued at 1/s.

Claims: **273 recorded, 273 read back after a world save, 0 from the startup reading, 0 waiting** —
the settle path ran end to end with no fallbacks.

After the owner quit cleanly, the whole cache was compared against the settled file: **35 of 10,423
claims stale, 0.34%**, against 399 for the equivalent measurement a session earlier. The stale ones
cluster where he stood — 16% of claims within three chunks of him, 4% within seven, and 0.28% of
the 8,190 beyond forty — which is his own loaded terrain changing while he was in it, plus the
background arms waking dormant columns further out.

## Delivered

**0.4.118** (`0c9ac69`, SHA-256 `DBE9476C087849AB9A459B3D2CEFFCE73BC7C4B2F668377DE857E58C4CAE49DA`,
681,469 bytes, 43 entries, verified and copy-installed):

- `LodSavegameScan` hashes the landscape part of a chunk row and filters block entities to the
  classes a capture reads; unparseable rows and unreadable class names fall back to hashing
  everything.
- `LodServerCaptureSystem` resolves those classes from the block registry at world start.
- `LodStore` stores a fingerprint rule version, drops claims when it changes, and drops claims
  alongside sections in a schema purge.
- Checks: 25,341 assertions.

**0.4.119** (`3dd8fb3`, SHA-256 `CEDC3EACE8BA30F163C178E99EC70461156F4908C4EC081C6084F9AA9E8BFEAC`,
683,098 bytes, 43 entries, verified and copy-installed):

- The status line prints after the sweep finishes while its background arms are moving, and carries
  a claim clause.
- Claims are taken from a re-read of the savegame after a world save rather than from the startup
  index, with the three fallbacks above.
- `LodPipeline.IsColumnQueued` so a claim is never taken while a capture is outstanding.
- The rule-version purge announces itself whenever claims are really dropped.
- Checks: 25,352 assertions.

**Measurement tools**, kept at the owner's request after the session's numbers were taken:

- `scripts/savegame-churn.py` - two settled snapshots of one world in, what changed and why out,
  by protobuf field and block-entity class. Compares raw bytes rather than reimplementing the
  mod's hash, so a rule change cannot invalidate it.
- `scripts/fingerprint-dump` - dumps the mod's own column fingerprints by calling
  `LodSavegameScan` itself. An earlier Python copy of the rule reported every claim as stale the
  moment 0.4.118 shipped, which is why this one runs the shipping code.
- `scripts/claim-check.py` - a dump against a cache's stored claims: how many the savegame still
  agrees with, optionally split by when each claim was written.

Re-run against the owner's `test2` after this session they reproduce its headline figures: 10,423
claims, 35 of them stale, 0.34%.

Measurements not reduced to a tool: the section-write correlation, the spatial clustering tests and
the byte-share breakdown were one-off analyses over the same files.

## Decisions

- **Exclusion, not inclusion.** An unrecognised protobuf field counts as a change. A future game
  version can add something visible without this silently ignoring it.
- **Decors excluded.** Owner's call, 2026-09-20: ground gaining decor is being worked on for other
  reasons anyway. Rejected alternative: keep them, on the grounds that the capture ignoring them
  today is not a guarantee about tomorrow.
- **Chisel classes from the registry, not a constant.** Rejected alternative: match `MicroBlock` and
  `Chisel` literally, which drops a modded microblock subclass out of the fingerprint silently.
- **A rule version rather than a schema bump.** A bump wipes the sections, which are still valid;
  only the claims lost their meaning.
- **Claims settle after a save; the tail still falls back.** Rejected alternative: never claim
  without a post-save read, which would have left a whole save interval's captures unclaimed at
  shutdown — 399 reloads would have become thousands.
- **No storage-pressure or rate change.** The audit's cost was never in question here.
- **A4 is still the owner's open decision**, though this session's measurement is the argument for
  keeping its save-hook half and dropping its clean-shutdown marker.

## Traps

**Section.ModifiedMs is uptime, not wall clock.** Trigger: correlating cache rows with log or claim
timestamps. Failure: `ColumnFingerprint.ModifiedMs` is Unix milliseconds while `Section.ModifiedMs`
is `Environment.TickCount64`, so a naive difference is about 1.79 billion seconds and rows written
before the last reboot carry a *larger* number than ones written after it. Safer action: convert
through the machine's boot time and restrict the comparison to a single boot.

**An announcement gated on a marker that first-run caches do not have.** Trigger: copying the
schema-purge pattern, which stays quiet when no version row exists because a brand new database has
nothing to discard. Failure: the first cache to meet the new fingerprint rule has no rule marker and
every claim it holds is about to be dropped — exactly the run worth announcing — so 0.4.118 dropped
2,323 claims on `test2` in silence. Safer action: gate the announcement on rows actually deleted.

**A status line that only speaks while a job is unfinished cannot report what starts when it
finishes.** Trigger: any arm that begins at completion. Failure: the audit and snow refresh ran nine
minutes with no log output. Safer action: print on evidence of work done, not on a job being
incomplete. This is G237's shape a second time.

**A re-read queued behind a 16/s audit waits minutes.** Trigger: reusing a background queue for work
that gates something else. Failure: claims from the 20:54 save were not written until 20:56:39, and
a session ending in that window falls back to the old-style claim. Safer action: give claim re-reads
priority over audit re-reads, or a queue of their own.

**The mod's own background arms invalidate its claims.** Trigger: any feature that loads dormant
terrain. Failure: the snow refresh at 1/s and the audit's recaptures wake dormant columns, which the
engine then updates and saves, which disputes the claim for those columns. Safer action: expect a
floor on claim staleness proportional to how much dormant terrain the mod itself touches, and do not
read it as a defect in the claim path.

## Flagged and unverified

Judgement calls awaiting human review:

- Decors are in the dropped list on the owner's reasoning; if a future capture reads them the list
  must move with it, and nothing in the build enforces that beyond a check asserting today's rule.
- Claim re-reads sharing the audit's queue is left as measured rather than fixed.

Claims that lack the evidence level to be treated as established:

- **That a disputed claim's recapture is wasted work** is measured for 48 of 162 columns and argued
  from `ChunkDirty` semantics for the rest. Not proven.
- **The settle path is thinly tested.** The 0.4.119 session captured 273 columns; a session with a
  large sweep behind it would be the real test.
- **The 0.34% post-quit figure is not a like-for-like successor to 399.** That session captured far
  fewer columns; per column captured, 20 of 261 claims went stale later, though none was stale when
  made.
- **No FPS or frame-time measurement was taken for either build.** The owner stood still
  deliberately. The snow refresh's ~12,000-column backlog at 1/s is new background load that no
  performance run has yet seen.
- **17.8% is a lower bound on capture-irrelevant churn.** Any change to the packed block bytes was
  counted as relevant, and some of those cannot alter a distant section.
