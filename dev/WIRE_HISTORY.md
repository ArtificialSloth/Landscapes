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

## Section blob-format ledger

| Blob format | Current schema | Meaning |
|---|---|---|
| 4 | 6 | Deflated section containing block-code palette entries, untinted colors, material flags, per-column run counts, packed runs, captured-column bits, and optional append-only observed-snow provenance/coverage bitsets. Tint and texture slots are resolved live and are not persisted. |

Earlier blob formats existed in the original development history but have not yet been reconstructed from Git. Do not invent their meanings from the current reader; add rows only from source history or preserved release evidence.

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

## Rules for compatibility changes

When changing protocol, blob format, or schema meaning:

1. Decide whether old readers can interpret the new meaning safely.
2. Update the code constant and serialization/deserialization behavior together.
3. Add an append-only ledger row here.
4. Add round-trip, rejection, and mixed-version tests where applicable.
5. Update `STATUS.md` and the session record.
6. Ensure destructive cache invalidation reports what it discarded.
7. Run `dev/DocCheck.ps1` and the fast checks.
