# Vintage Horizons — compatibility ledger

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

## Section blob-format ledger

| Blob format | Current schema | Meaning |
|---|---|---|
| 4 | 6 | Deflated section containing block-code palette entries, untinted colors, material flags, per-column run counts, packed runs, captured-column bits, and optional append-only observed-snow provenance/coverage bitsets. Tint and texture slots are resolved live and are not persisted. |

Earlier blob formats existed in the original development history but have not yet been reconstructed from Git. Do not invent their meanings from the current reader; add rows only from source history or preserved release evidence.

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
