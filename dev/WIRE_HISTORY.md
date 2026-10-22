# Doden's Landscapes — compatibility ledger

> Tier 3: append-only history for externally persisted or transmitted meanings. Code constants are current authority; this ledger explains how and why they moved.

## Current compatibility numbers

- **Assist protocol:** `1`
- **Section blob format:** `4`
- **Database schema meaning:** `6`

## Assist protocol ledger

| Protocol | Released | Meaning |
|---|---|---|
| 1 | 0.2.0 | Optional server handshake, chunked key manifest, batched section request, and one stored section blob or explicit empty refusal per response. |

Current message types:

- `AssistHello`
- `AssistWelcome`
- `AssistKeyManifest`
- `AssistSectionRequest`
- `AssistSection`
- `AssistWorldGenRequest`
- `AssistWorldGenStatus`
- `AssistSilhouetteRequest`
- `AssistSilhouetteManifest`
- `AssistSilhouetteTileRequest`
- `AssistSilhouetteTile`
- `AssistImportStatus` (0.5.1; sent only to a client that asked)

Protobuf fields are append-compatible. Bump the protocol when an existing message or field changes meaning, required sequencing changes, or old peers would interpret a valid exchange incorrectly.

**Append-compatible protocol-1 additions for live refresh (0.4.40–0.4.49).**
`AssistServerSettings.RefreshColumnsPerSecond` is field 16; an older peer reads its normal
protobuf default of zero. `AssistKeyManifest.Refresh` is field 4. False retains the original
monotone “the server has this key” manifest meaning. True says the listed existing level-zero
rows now have newer durable contents and may replace a client's older local copy. Older clients
ignore the field and retain their previous one-shot behavior; they do not misinterpret the blob
or required sequence. New clients route the replacement through the same bounded request and
format-4 blob path, guard install by the submitted content revision, and retire the update route
only after the replacement's persistence revision is acknowledged. These additions therefore do
not require a protocol bump.

**Append-compatible protocol-1 additions for Generate World (0.4.74–0.4.75).**
`AssistWelcome.Features` is field 7 and `CanControlWorldGeneration` is field 8. Feature bit 0
advertises Generate World; bits 1 and 2 are reserved for frontier generation and silhouette tiles
respectively, in planned landing order. `AssistServerSettings` fields 17–23 report the enabled,
maximum/default radius, queue target, in-flight limit, dirty-section limit and oldest-dirty limit.
The `AssistWorldGenRequest` and `AssistWorldGenStatus` message types were appended after the five
existing channel registrations; older peers neither send nor register them. Status field 28,
`Action`, was appended in 0.4.75 so a client can keep the exact control result visible across later
polls. Old protobuf peers ignore every unknown field, a zero `Features` value means unavailable,
and no existing message, field or required sequence changed meaning. The compatibility numbers
therefore remain `1 / 4 / 6`.

**Append-compatible protocol-1 additions for horizon silhouettes (0.4.77).** Four message types
were appended after `AssistWorldGenStatus`, in this order on both sides:
- `AssistSilhouetteRequest` (client to server): enabled, reach, mod version.
- `AssistSilhouetteManifest`: registered, unused in 0.4.77.
- `AssistSilhouetteTileRequest`: registered, unused in 0.4.77.
- `AssistSilhouetteTile` (server to client): tile X and Z, revision, payload, retryable.

Features bit 2 is now advertised; in 0.4.77 only singleplayer and LAN-hosted servers advertise it.
A client sends the request only when the bit is set, and older peers neither register nor send the
new types.

The tile payload is its own format, version 1:
- one format byte;
- then, deflated, a 2-bit state for each of the 128x128 cells of a 512-block L3 footprint (unknown,
  captured, preview or accelerator), four to a byte in row-major order;
- followed by the little-endian height of every known cell, in the same order.

No existing message, field or sequence changed meaning. The compatibility numbers therefore remain
`1 / 4 / 6`.

**Silhouette changes without a wire change (0.4.78–0.4.83).**
- The server still pushes tiles in payload format 1.
- The manifest and tile-request types stay registered and unused.
- Features bit 2 is still advertised only by singleplayer and LAN-hosted servers.
- Since 0.4.83 the bit means "this server offers silhouettes", not "this server holds a silhouette
  builder". A dedicated server measuring heights (`VINTAGEHORIZONS_SILHOUETTE_MEASURE`) holds a
  builder, advertises nothing and ignores silhouette requests.
- The server setting `EnableSilhouettes` is configuration and is not mirrored into
  `AssistServerSettings`.

**Silhouette tile store (0.4.80).** A separate server file,
`ModData/vintagehorizons/<savegame>-silhouette-server.db`. It is created the first time a player in
that world asks for silhouettes.
- Its `Meta` table carries its own `SchemaVersion`, `1`.
- Its `Tile` table holds `(TX, TZ, Revision, Data, ModifiedMs)`, where `Data` is the tile payload
  above.
- A write merges into the stored row. The stored copy fills the cells the new one lacks, and those
  it knows from a strictly better source.
- A different schema value empties the table on open, with a count in the log.

It shares nothing with the section cache, so the section blob format and schema meaning are
unchanged.

**Comparison files (0.4.84, developer tools).** `/vhsilhouetteserver presample` and `compare` write
gzip CSV files under `ModData/vintagehorizons/silhouette-compare/`:
- `<world>-presample.csv.gz`, magic line `# vintagehorizons silhouette presample 1`;
- `<world>-map-<utc>.csv.gz`, magic line `# vintagehorizons silhouette map 1`.

`compare` reads a presample only when its magic line matches. Nothing else reads these files, and
they share nothing with the section cache or the tile store.

**Accepted accelerators and final silhouette transport (0.4.85-0.4.86, no protocol bump).**

0.4.85 appends `AssistServerSettings.SilhouetteUseAccelerators` at field 24. It reports whether the
server permits an installed accepted sampler; it does not require either sampler on a peer.

0.4.86 appends:
- `AssistServerSettings.SilhouetteReachBlocks` field 25 and `SilhouettePreviewsPerSecond` field 26;
- `AssistSilhouetteRequest.TransportVersion` field 4 and `SessionId` field 5;
- `AssistSilhouetteManifest.SessionId` field 5;
- `AssistSilhouetteTileRequest.SessionId` field 2;
- `AssistSilhouetteTile.SessionId` field 6 and exact requested `Key` field 7.

Silhouette transport generation `0` is the 0.4.77-0.4.85 direct-push prototype. Generation `1`
uses the already registered manifest and tile-request messages: the server offers tile revisions,
and the client asks for missing revisions. The server echoes the request's world-session id on
every manifest and tile answer. A current client ignores stale-session replies. Every requested key
receives a success, explicit refusal or retryable response; the exact `Key` lets malformed requests
receive a terminal answer even when tile coordinates cannot reproduce them.

An old server reads the new request fields as their protobuf defaults and continues pushing tiles.
A current client recognises `SessionId == 0` as that legacy reply. An old client sends transport
generation 0, so a current server retains direct push for it and does not require a manifest/request
sequence it cannot perform. Unknown server-setting fields keep their ordinary defaults. No existing
message, field, payload or required old-peer sequence changed meaning, so assist protocol, section
blob and database schema remain `1 / 4 / 6`; the silhouette payload and tile-store schema remain 1.

**Append-compatible protocol-1 additions for section sync (0.4.126-0.4.127).** The
terrain-change plan's Phase B. No message type was added and none changed meaning:
- `AssistHello` field 3 `SectionSync`, 4 `SyncGeneration` and 5 `SyncVersion`: a client that keeps
  a record of the server's cache says so (`LodAssist.SectionSyncVersion` = 1) and names the
  generation and version it caught up to, or an empty generation for none. Older clients send none
  of these, which reads as a client that does not sync.
- `AssistWelcome` field 9 `CacheGeneration`: set only when the server versions its cache and the
  client asked to sync. Empty means no versioned span follows, which is what every older server
  sends.
- `AssistKeyManifest` field 5 `VersionFrom`, 6 `VersionTo` and 7 `Versions`: a refresh manifest with
  `VersionTo` above zero is a versioned span - every level-0 section whose version lies in
  (`VersionFrom`, `VersionTo`], with each key's version, chunked with the existing
  `Sequence`/`Last`. A span may be empty; its `Last` chunk still ends it. `VersionFrom` -1 asks the
  client to re-check every section it holds.
- `AssistSection` field 4 `Version`: the version of the row a served blob was read from, 0 from an
  unversioned server.

`Refresh` (field 4) keeps its meaning and is still set on every span, so an older client reads a
span as the refresh it always handled. On the server, the refresh keys now come from version queries
over the cache rather than from an in-memory set of persisted keys. Integrated play sends no
`SectionSync` and keeps using the sibling cache's rowids. Mixed peers each behave as their own
version did, and the pairings are pinned by `WireFormatChecks` against the 0.4.125 shapes. The
compatibility numbers therefore remain `1 / 4 / 6`.

**The mod is renamed and its channel with it (0.4.138, Session 113).** The network channel name
changed from `vintagehorizons` to `dodenslandscapes` (`LodAssistProtocol.ChannelName`), with the mod
id. No message, field or sequence changed, so the protocol stays `1`, but peers on either side of the
rename never meet: a 0.4.138+ client sees a 0.4.137- server as one without the assist, and the
reverse. Other names that left the wire or the disk with the id: the code file
(`DodensLandscapes.dll`), the asset domain, `ModData/dodenslandscapes`, and the
`dodenslandscapes.json` and `dodenslandscapes-server.json` settings files, to which 0.4.138-0.4.159
moved the earlier names once at first start (never over a newer file, and not while the old mod id
was enabled). 0.4.160 removed that move: the old folder and files are also the original Vintage
Horizons' own, so nothing under the old id is read, moved or deleted any more (G276, settled decision
60). Names inside stored formats were kept on purpose: the cache's database type code
`vintagehorizons lod cache`, the height-sample file headers, and the game start-up file's
`VintageHorizons.AllCoresCollector` marker and `.vintagehorizons-backup` suffix. Blob format and
schema meaning are unchanged, so moved caches open as they were.

**Generate-page coordinates are spawn-relative (0.4.160, Session 116).** `AssistWorldGenRequest`
gains `SpawnRelative`, field 9. True, which every 0.4.160+ client sends, says `X` and `Z` are block
coordinates measured from the world spawn, as the game's coordinate display shows them, may be
negative, and count only for the Coordinates centre. False, the default an older client sends, keeps
the old meaning: the internal block position, with a negative number meaning "not given"
(`LodSpawnCoordinates.RequestedCentre`). Append-only, so the protocol stays `1`.

**A server bounds what it accepts (0.4.161, 0.4.163-0.4.164, Session 116).** No message, field or
sequence changed, so the protocol stays `1`, but the reply rule did for clients outside the protocol's
bounds: the server ignores, without any reply, an `AssistSectionRequest` from a connection that has not
said hello or carrying more than 64 keys, keys past a player's 256 queued, a second `AssistHello` on one
connection, an `AssistSilhouetteTileRequest` of more than 64 keys or past 256 queued tile keys, and an
unchanged `AssistSilhouetteRequest` within 5 s of the last one acted on. Every client this mod has
shipped stays within these bounds (16 keys in flight, one hello per join), so for them every request is
still answered (`LodAssistAdmission`, `LodHelloGate`; G288, settled decision 61).

**The welcome says where Farseer's heights come from (0.4.165, Session 117).** `AssistWelcome` gains
`SilhouetteHeightSource`, field 10, an `EnumSilhouetteHeightSource` sent as an int: 1 the server builds
from engine previews because it found neither supported sampler mod, 2 an accelerator (Algernon's
Terrain Sampler or Watersheds) answers, 3 the server config turned accelerators off. 0, which every
older server sends and every server that offers no silhouettes sends, means unknown. The client shows
its red no-sampler note only for 1 while the server offers silhouettes. Append-only, so the protocol
stays `1`; an older client ignores the field.

**Client settings file (Session 117, 0.4.166-0.4.168).** `dodenslandscapes.json` gains
`ShowDeveloperOptions` (false: the settings window hides its Developer tab) and `PresetPromptShown`
(false: the first-run preset prompt shows once at the next login, existing installs included), and no
longer uses `ShowAdvancedSettings`, which an older file keeps harmlessly. A new install starts at the
Quality preset: `FarViewDistanceCap` and `SilhouetteReachBlocks` default to 8,192 (both were 4,096); an
existing file keeps its own values. No dial's meaning or range changed.

**Append-compatible protocol-1 additions for the import status (0.5.1, Session 119).** No existing
message or field changed meaning:
- `AssistHello` field 6 `ImportStatus`: `LodAssist.ImportStatusVersion` (1) from a client that shows the
  import status; every older client sends 0.
- `AssistImportStatus`, a new server-to-client message type registered after `AssistSilhouetteTile` on
  both sides (G305), sent only to a client whose hello asked: field 1 `Active`, 2 `Finished`, 3
  `BandBlocks` (512), 4 `PendingByBand` (packed int32, 65 bands nearest first, the last holding
  everything from 32,768 blocks out), 5 `ColumnsPerSecond` (0 before 30 s are measured), 6
  `PendingTotal`. Every 5 s while the sweep's load pass runs, then once with `Finished`.
- `AssistServerSettings` field 7 `SweepRadiusChunks` is retired: a 0.5.1 server sends 0 because the
  sweep covers all explored land. An older client's diagnostics window then shows "0 chunks"; a 0.5.1
  client shows "all explored land" for 0 and the old square for anything else.

**Server settings file (0.5.1).** `dodenslandscapes-server.json` no longer carries `SweepRadiusChunks`.
An existing file's value is read once: 0 (the documented off switch) becomes `SweepSavegame: false`,
anything else is ignored, and the key is not written back. `SweepSavegame` alone switches the sweep.

## Section blob-format ledger

| Blob format | Current schema | Meaning |
|---|---|---|
| 4 | 6 | Deflated section containing block-code palette entries, untinted colors, material flags, per-column run counts, packed runs, captured-column bits, and optional append-only observed-snow provenance/coverage bitsets. Tint and texture slots are resolved live and are not persisted. |

Earlier blob formats existed in the original development history but have not yet been reconstructed from Git. Do not invent their meanings from the current reader; add rows only from source history or preserved release evidence.

**Readers refuse blobs no real section could be (0.4.163, no format change).** A format-4 blob that
unpacks to more than 64 MB, claims more than 4,096 runs in one column, or a palette block code over
1,024 bytes is read as unusable, the same as a corrupt one; the checks run before memory is set aside
(`LodStore.Deserialize`, `LodBoundedReadStream`; G291). Real sections come to a few MB, so no stored or
served row is affected.

**What a palette code names at a chiselled position (0.4.62, no format change).** Capture records
a chiselled block as its most visible material, so a format-4 row written from 0.4.62 on holds, for
example, a plank code where the world holds a chisel. Every reader already resolves an ordinary
block code, so neither the blob format nor the schema meaning moved. Rows written earlier still name
the chisel, and a current client repairs their near-white stored colour to another of the block's
own textures or neutral grey on load, until that ground is captured again.

**Material-flag bits within format 4.** `0.3.140` began writing flag bit 2 (`FlagHalf`, terrain
that fills only the lower half of its cell). The format was deliberately NOT bumped, on two
grounds that must both hold before any future flag bit does the same: older readers test named
bits individually and ignore unknown ones, so a set bit 2 cannot be misread as water, thin cover
or skip; and flags are recomputed from the live block on load (`LodStore.Reclassify`), so the
stored byte is a cache of a derived value rather than the authority. A flag bit that fails either
test - one an old reader would act on, or one that cannot be re-derived from the block registry -
requires a format bump and a ledger row.

`0.3.163` began writing flag bit 4 (`FlagCutout`, a block the game draws see-through and that
therefore covers nothing) under the same two grounds, both re-checked: no shipped reader tests
bit 4, and the bit is derived from `Block.AllSidesOpaque` under a Plant material guard, so
`LodStore.Reclassify` recomputes it on every load and an existing cache gains it with no
re-exploration. Bit 4 was previously described as free; it once held a tint class, superseded by
the persisted-nothing `TintSlot`, and no shipped format ever wrote it with that meaning. The
remaining free bits at that point were 32, 64 and 128.

`0.4.40–0.4.49` began using flag bit 32 (`FlagSnowLayer`) for vanilla snow material and bit 64
(`FlagSnowCovered`) for blocks whose live `Variant["cover"]` is snow. Both satisfy the same
derived-flag rule: readers that predate them ignore the named bits, and current readers recompute
them from the live block registry. Bit 128 remains free.

**Append-only snow tails within format 4.** Version 0.4.43 first appended one 4,096-bit map saying
which LOD columns came from an authoritative loaded/savegame capture rather than a generated peek.
A later correction appended a second 4,096-bit map carrying the paired snow-covered yes/no answer.
This separation is required because a thin snow material can disappear during mip majority
selection while areal snow coverage must survive. Old format-4 readers stop after captured bits
and ignore both tails. Current readers accept no tail as legacy unknown; they also clear the first
tail when the second is absent, so a 0.4.43-only row cannot claim that a downsampled-away snow
layer proves bare ground. A partial tail is corrupt and rejected. The two complete maps add 1,024
uncompressed bytes per section before deflate and do not change terrain-run meaning, so blob
format and schema meaning remain 4/6.

## Database schema-meaning ledger

| Schema | Released | Meaning |
|---|---|---|
| 6 | 0.2.0–0.2.1 | Stored palette colors are untinted and block semantics are reclassified from live registry data. Rows from other schema meanings are purged rather than interpreted. |

The database table layout may remain SQL-compatible while `SchemaVersion` changes because the stored values acquired new semantics. A schema-meaning bump intentionally invalidates existing cache rows.

Session 105 (0.4.118) changes what a `ColumnFingerprint` value MEANS without changing the table or the schema version. The hash now covers only the part of a stored chunk row a capture reads - blocks, fluids, and the block entities whose classes the block registry reports as microblock types - and drops block light, light saturation, entities and their count, the block-entity count, mod data, server mod data, light positions, decors and the placed/removed counters. A row that does not parse as that shape is hashed whole, which is the previous behaviour. Because the meaning changed, a value stored under the old rule can never match and would report its column as changed forever: the cache therefore carries `Meta.FingerprintRule` (now `2`, where the whole-row rule was `1`), and every stored claim is deleted when the stored rule differs, with a log line naming how many went. This is deliberately NOT a schema-meaning bump: the captured sections remain exactly as valid as before, and bumping `SchemaVersion` would discard them for nothing. The same session also makes a schema purge delete `ColumnFingerprint` alongside `Section`, because a claim that survives the deletion of the section it describes lets the sweep skip terrain the cache no longer holds.

Session 105 (0.4.119) changes WHEN a claim is written, not its meaning: a durable capture waits for the next world save, and the claim is then taken from a fresh read of the file rather than from the startup index. Stored rows are unchanged in shape and rule, and a session that ends before that re-read still writes the index-time value.

Session 109 (0.4.126) versions the server cache's sections, without a schema-meaning bump:
- **Server caches only** (`-server.db`; a client cache never gets these): `Section` gains
  `Version INTEGER NOT NULL DEFAULT 0` by `ALTER TABLE ADD COLUMN`, plus the covering index
  `SectionVersions (Detail, Version, SX, SZ)` (G255). Rows written before it read as version 0.
  Every checkpoint transaction stamps its rows with one new version and writes the counter to
  `Meta.SectionVersion` in the same transaction, so a reader that sees the counter sees every row at
  or below it.
- `Meta.SectionGeneration` names the cache's lineage. It is minted when the cache is first versioned,
  again after a format purge (which now deletes it), and again when the insert trigger
  `SectionLegacyWrite` has set `Meta.LegacyWrite`: the trigger marks any row written with version 0,
  which is how a build from before section sync writes. That costs such an older build one small
  Meta write per row, and the next start re-mints the generation so every client re-checks.
- **Client caches:** the table `AssistOwed (Detail, SX, SZ, Version)` holds the held sections a server
  announced as changed and the client has not yet merged and saved, and `Meta.AssistGeneration` and
  `Meta.AssistVersion` record how far it caught up. A span's owed rows and the version it reaches are
  written in one transaction. The table is created in every cache, empty on the server's. A format
  purge clears the table and both Meta keys with the sections they describe.

An older build ignores the column, the index and the new table, and an older reader of a server cache
reads `Section` exactly as before. The blob format and schema meaning are unchanged (4/6).

Session 103 (0.4.113) adds the table `ColumnFingerprint (CX, CZ, Fingerprint, Rows, ModifiedMs)`: per chunk column, a 64-bit content hash of what the savegame held at the moment this cache proved it had captured that column, so a later sweep can skip it. It is additive and created with `CREATE TABLE IF NOT EXISTS`. The Section table, the blob format and the schema meaning are unchanged (4/6); older builds never read the table, a format purge clears it with the sections, and a missing row reads as "assume changed", which is exactly the behaviour before the table existed. Deliberately not a column on `Section`: that is keyed by `(Detail, SX, SZ)` and this by chunk column, which are different key spaces at different granularities. Deliberately not a schema-meaning bump either, because a bump wipes the cache and the cache is the baseline any measurement of this feature has to be made against. The fingerprint stays advisory: it may cause work to be skipped, never a claim about content, and it is written only once a column's capture has reached the durable row.

Session 90 (0.4.68) adds the table `SiblingMerge (Detail, SX, SZ, SourceRowid)`: for a key the client holds, the rowid of the server-side sibling-cache row its stored copy already contains. It is additive. The Section table, the blob format and the schema meaning are unchanged (4/6); older builds never read the table, a format purge clears it with the sections, and deleting an unreadable section deletes its record. The server half's cache contains the table empty, because both halves use `LodStore`.

## Rules for compatibility changes

When changing protocol, blob format, or schema meaning:

1. Decide whether old readers can interpret the new meaning safely.
2. Update the code constant and serialization/deserialization behavior together.
3. Add an append-only ledger row here.
4. Add round-trip, rejection, and mixed-version tests where applicable.
5. Update `STATUS.md` and the session record.
6. Ensure destructive cache invalidation reports what it discarded.
7. Run `dev/DocCheck.ps1` and the fast checks.
