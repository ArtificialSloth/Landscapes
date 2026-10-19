# Session 116 — Pre-release review and its fixes

**Date:** `2026-10-18..19`
**Branch/commit:** `main`, checkpoints `a6e0024` (0.4.157) to `e057a00` (0.4.164); nothing pushed
**Mod version:** `0.4.164`
**Assist protocol / blob / schema:** `1 / 4 / 6` (unchanged; one append-only field, `AssistWorldGenRequest.SpawnRelative` = 9)

> Session records are Tier 3 history. Write narrative as needed, but preserve the four required tail sections so future harvesting remains mechanical.

## Context and investigation

Before this review, three release-polish builds landed after Session 115's documentation: a new mod
icon (0.4.157, a drawn telescope badge the owner did not like; 0.4.158, a round crop of his own
sunset screenshot over distant terrain) and a one-sentence Mod Manager description (0.4.159).

With the release close, the owner asked for a review of the whole project in three passes - things
that are broken, security, and performance left on the table - reported after each pass. It was
report-only until he chose what to fix. Evidence: a Release build (0 warnings) and the fast tier
(26,355 assertions, 0 failures) at 0.4.159; reading the mod's source, concentrated on the entry
points, network handlers, storage and the savegame reader; decompiling the game (1.22.7) where a claim
depended on it; and the owner's client logs of 2026-10-18 for performance. Nothing was run in game.
The full findings, with locations and a progress table, are in `notes/pre-release-review-2026-10-19.md`
(private, git-ignored).

## Work narrative

### 1. Pass 1: broken things

- **B1 - the rename move took over another mod's data.** `ModIdentity.TakeOverLegacyData` moved
  `ModData/vintagehorizons` and the two `vintagehorizons*.json` settings files into this mod's names at
  every start, guarded only by "is that mod switched on". `vintagehorizons` is also AliasFactory's
  public mod id, so after release the move could only ever touch other people's data, and a moved
  cache whose format label is not `6` is then emptied by `LodStore.PurgeOutdatedData`. The owner's own
  data had moved in 0.4.138 (G276, revised).
- **B2 - typed coordinates.** The Generate page, `/dlworldgen` and `/dlgen` read typed x z as the
  map's internal block position. The game's coordinate display shows position minus
  `DefaultSpawnPosition` (`HudElementCoordinates`), so numbers off the screen are small and often
  negative: the page clamped negatives to 0, the server refused them, and positive ones pointed near the
  map corner; `/dlworldgen` writes real terrain (G287).
- **B3 - the savegame reader blocked saves.** Its whole-table scans were one SQLite query with
  `Thread.Sleep` pauses inside it, documented as handing the file back to the game. A query holds the
  read lock until it closes. Under the default `CorruptionProtection` (WAL) that only held up
  checkpoints; with it off the game opens its savegame with `journal_mode=Off`, and its chunk saves
  waited the whole scan and failed after Microsoft.Data.Sqlite's 30-second timeout (G286).
- **B4 - the OpenGL 4.3 requirement was unstated.** Macs (OpenGL 4.1) and some older laptop graphics
  fail the GPU check and draw no distant terrain; the README did not say so, and its `.dlgpu off` row
  claimed a fallback renderer that does not exist. Capture keeps running when the check fails; that
  was left as is, because a later passing check (`.dlgpu on`, a driver) then shows the whole cache.
- **Small:** command dials parsed decimals with the PC's language (B5); the join log line always said
  "render distance: unlimited" (B6).
- **Clean:** the release zip (no `.pdb`, no personal paths), guarded Harmony patches, exact-case shader
  names and includes, the read-only savegame connection, the start-up-file edit.

### 2. Pass 2: security

- **S1 - request floods.** The server promises an answer for every requested section key; past its
  256-key queue it refused each extra key with its own message at once, on the owning thread, from any
  connected client, greeted or not. The game accepts a 1 MB packet from a player
  (`TcpNetConnection.MaxPacketSize`), room for 100,000+ keys (G288).
- **S2 - hello floods.** Every hello re-sent the whole key manifest and queued a full span scan, with
  no limit; the span queue had no cap.
- **S3 - the section list reveals activity.** The serve radius limits which terrain is sent, not the
  list: every modded player receives the whole world's keys at 64x64-block resolution and a notice
  whenever one changes - with a modified client, a live map of where others explore and build.
- **S4** - a section blob's own size claims were believed before allocating (up to 2 GB of runs, or a
  palette name of any length): a hostile server could freeze or crash a client.
- **S5** - silhouette requests: a log line per request, an unbounded tile queue, and per-key refusals
  with no demand.
- **Clean:** every server command requires `controlserver` and the Generate page's actions are
  re-checked on the server; queries are parameterised; file names from world names are sanitised;
  nothing from the network runs as a command; developer switches are environment-only.

### 3. Pass 3: performance

- **P1 - rough first minutes.** All five sessions in the owner's 2026-10-18 logs show the server half
  loading 150-1,000 savegame columns in the first 1-3 minutes; in the 22:19 session those minutes had
  a 1% low of 50-59 FPS, worst frames 360-420 ms, 8 full collections a minute and the chunk thread
  allocating 84-100 MB/s, against a 1% low near 190 FPS once the sweep stopped. Correlation only.
- **P2** - each 50 ms tick the request pumps copied and fully sorted every wanted section with a
  comparison computing both distances per compare, to pick 4 or 16.
- **Clean:** the renderer's per-frame work is budgeted (full-grid scans run on movement thresholds or
  timers), no LINQ in drawing paths, workers below normal priority.

### 4. The fixes, one build at a time

- **0.4.160 (B1, B2).** The move and its three `StartPre` calls are gone; a check fails if any source
  spells out the old id or calls the move again (`ModIdentityChecks.NeverTouchesTheOriginalModsData`).
  `LodSpawnCoordinates` converts typed and shown positions; commands detect a missing coordinate with
  the parser's own flag instead of -1; the page sends `SpawnRelative = true` and an older client's
  request keeps its old meaning; centres are floored, so a position past the map edge no longer
  rounds into chunk 0 and passes the bounds check.
- **0.4.161 (S1, S2).** `LodAssistAdmission`: a request is looked at only from a greeted client and
  only up to 64 keys (four times the honest 16), dropped on the network thread when larger; keys past
  the 256 queue go unanswered instead of refused one by one. `LodHelloGate` answers one hello per
  connection on the network thread. One warning per misbehaving connection.
- **0.4.162 (B3).** `LodSavegameReader.ReadTable` reads `chunk` or `mapchunk` in time-budgeted batches,
  each its own query from just past the last position, in position (row id) order, so the file is
  still one sequential pass. A check shows the game's write failing during one long query and
  succeeding between batches. README: a Requirements section (OpenGL 4.3, no Macs) and the corrected
  `.dlgpu off` row (B4).
- **S3 - decided, not built.** The owner first chose to filter by radius. Filtering breaks section sync
  (Session 109): a client's caught-up record would move past changes it was never told about, and it
  drops owed sections the list omits; doing it correctly needs the server to remember held-back changes
  per player across sessions (G289). Told this, the owner chose to document it for release: README
  "Sharing the server's cache" names `EnableServing: false`. The full fix is a post-release proposal.
- **0.4.163 (P2, B5, B6, S4, S5).** `LodNearestFirst` reuses one key list and distance buffer and
  sorts by precomputed distance, and both pumps skip building the list when every slot is taken (same
  order, checked against the old sort). `TryParseSetting` reads "1.5" and "1,5" alike for every command
  dial. The join line names the real cap. Sections may unpack to 64 MB, a column claim 4,096 runs and a
  block code 1,024 bytes, checked before allocating (`LodBoundedReadStream`; G291). Silhouette tile
  requests over 64 keys are dropped on the network thread, a player's tile queue stops at 256, and an
  unchanged repeat of a silhouette request within 5 s is ignored.
- **0.4.164.** The owner asked that silhouette requests are never ignored for good as players move.
  Source-traced: a moving player's reach never depends on the client asking again - every server tick
  re-centres each demand on the player's live position and offers the tiles that came into reach - and
  the honest client holds at most 16 tile requests. But 0.4.163 restarted the 5 s window on every
  ignored repeat, so a client repeating faster would have been turned away permanently. The window now
  runs from the last request acted on (`IsUnchangedRepeat`; G290).

---

## Delivered

- **0.4.157** (`a6e0024`): new mod icon, a drawn telescope badge. Not liked by the owner.
- **0.4.158** (`be83144`): mod icon from the owner's own sunset screenshot over distant terrain.
- **0.4.159** (`d947570`): one-sentence Mod Manager description.
- **0.4.160** (`f3efb97`): no legacy-data move; spawn-relative coordinates; request field 9.
- **0.4.161** (`a82a1f7`): request and hello admission limits.
- **0.4.162** (`bd12ffe`): batched savegame reads.
- **0.4.163** (`774e9a4`): nearest-first ordering, decimal parsing, join line, section decode limits,
  silhouette request limits.
- **0.4.164** (`e057a00`): the silhouette repeat window never extends itself.
- **Documentation:** README Requirements and corrected `.dlgpu` row (`184ca8e`); README "Sharing the
  server's cache" (`b335262`); this session's ritual.
- Every build verified (zip contents, version, code present, no personal paths) and copy-installed.
  None of 0.4.157-0.4.164 is recorded as played in this session. Tests: 26,277-26,408 assertions,
  0 failures.

## Decisions

- **The mod never reads, moves or deletes anything under the old id** (owner, 2026-10-19; settled
  decision 60). Rejected: a copy restricted to files proven to be the fork's.
- **Typed and shown coordinates are spawn-relative**, as the game's coordinate display.
- **A client past the protocol's bounds gets no reply** (settled decision 61): 64 keys per section or
  tile request, 256 queued per player, one hello per connection, unchanged silhouette repeats ignored
  within 5 s of the last one acted on.
- **For release the section list and change notices stay whole-world, documented for admins** (owner,
  2026-10-19; settled decision 62). Rejected for release: filtering by radius (breaks section sync, G289)
  and filtering change notices only (same). The full design is a post-release item.
- **Capture keeps running when the GPU check fails**, so a later passing check shows the whole cache.

## Traps

- G286: a SQLite query holds its read lock across pauses inside it; batch by closing the query.
- G287: the game's coordinate display is spawn-relative; never take a typed number as the internal
  position, and never use -1 as "not given".
- G288: an answer-every-key protocol is an amplifier unless the server bounds what it accepts.
- G289: filtering what a syncing client is told breaks its caught-up record.
- G290: a rate window restarted by ignored requests ignores a persistent sender for good.
- G291: a compressed blob states its own sizes; cap them before allocating.
- G276 revised: moving data under a renamed fork's old id takes over the original mod's data.
- `-F -` with a PowerShell here-string does not reach git's stdin; write the message to a file.

## Flagged and unverified

- Judgement calls awaiting the owner: the release number; pushing the commits since `1bea2c1`; the
  game-launching check tiers; P1 (measure the first minutes with `SweepSavegame: false`); the
  post-release S3 design.
- Unverified: none of 0.4.157-0.4.164 has been played in this session. The coordinate fix, the
  admission limits and the batched savegame reads are harness-tested only. A saved Generate-page
  centre typed before 0.4.160 was stored as an internal position and now reads as a spawn-relative one.
  P1's cause is a correlation in the logs, not a measurement.
