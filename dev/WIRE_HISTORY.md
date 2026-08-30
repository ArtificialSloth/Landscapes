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

## Section blob-format ledger

| Blob format | Current schema | Meaning |
|---|---|---|
| 4 | 6 | Deflated section containing block-code palette entries, untinted colors, material flags, per-column run counts, packed runs, and captured-column bits. Tint slots are resolved live and are not persisted. |

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
remaining free bits are 32, 64 and 128.

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
