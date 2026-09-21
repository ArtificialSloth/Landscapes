#!/usr/bin/env python3
"""What changed between two snapshots of one world, and why - per chunk column.

Answers the question behind the savegame sweep's workload: when a column's stored bytes
move, was it the landscape or something a captured section cannot show?  Takes two SETTLED
snapshots of the same world, finds every column whose `chunk` rows differ, decodes those
rows into protobuf fields, and reports which fields moved and which block-entity classes
were involved.

Session 105 used it on the owner's world (a frozen 2026-08-21 copy against the live file 25
days later): 13,754 of 62,390 columns moved, and 17.8% of those moved only for berry-bush
timers, mycelium, transient countdowns, creatures, light or other mods' chunk data.  That
measurement is what the landscape-only fingerprint rule in `LodSavegameScan` is based on.

This deliberately compares raw bytes rather than reimplementing the mod's hash, so it stays
correct when the hashing rule changes.  To check the mod's OWN fingerprints, use
`scripts/fingerprint-dump` (which runs the shipping code) and `scripts/claim-check.py`.

Usage:
    savegame-churn.py BEFORE.vcdbs AFTER.vcdbs [--json report.json]
                      [--examples N] [--limit-columns N] [--comparator PATH]

Read-only throughout; a non-empty WAL is refused because the snapshot is not settled.
Frozen sandbox seeds live under `.testdata/profiles/*/seed`, which the benchmark restores
before every run - so a profile's working copy is exactly one session away from its seed.
"""

from __future__ import annotations

import argparse
import collections
import hashlib
import importlib.util
import json
import random
import re
import sqlite3
import sys
import time
from pathlib import Path

# The P0 comparator already owns a verified protobuf decoder and the position layout.
def load_comparator(explicit: Path | None) -> object:
    candidates = []
    if explicit:
        candidates.append(explicit)
    candidates.append(Path(__file__).resolve().parent / "worldgen-save-compare.py")
    for path in candidates:
        if path.is_file():
            spec = importlib.util.spec_from_file_location("wsc", path)
            module = importlib.util.module_from_spec(spec)
            sys.modules["wsc"] = module
            spec.loader.exec_module(module)
            return module
    raise SystemExit("worldgen-save-compare.py not found; pass --comparator PATH")


MASK64 = (1 << 64) - 1

# What the LOD capture reads out of a chunk row.  Blocks, fluids and decors are the
# terrain itself; block entities carry the colour of chiselled blocks and clutter, which
# distant structures draw with (architecture settled decision 36).
CAPTURE_FIELDS = {1: "blocks", 14: "decors", 16: "fluids"}
BLOCK_ENTITY_FIELDS = {7: "block-entity count", 8: "block entities"}
# The only block entity the capture reads is BlockEntityMicroBlock: a chiselled block has
# no colour of its own, so capture records the material covering most of its outside
# (LodChiselStandIn).  Every other block entity's state is invisible to a captured section.
CAPTURE_BLOCK_ENTITIES = {"MicroBlock", "Chisel"}
BE_TOKEN = re.compile(rb"([A-Za-z][A-Za-z0-9_]{3,40})")


def be_entries(fields: dict) -> list[bytes]:
    return [payload for wire, payload in fields.get(8, []) if isinstance(payload, bytes)]


def be_token(entry: bytes) -> str:
    found = BE_TOKEN.search(entry[:64])
    return found.group(1).decode() if found else "unnamed"


def changed_block_entities(left: bytes, right: bytes, wsc) -> list[bytes]:
    """Entries present on one side and not the other, both directions."""
    before = collections.Counter(be_entries(wsc.parse_fields(left)))
    after = collections.Counter(be_entries(wsc.parse_fields(right)))
    return list((before - after).elements()) + list((after - before).elements())
# Everything else a chunk row carries.  None of it reaches a captured LOD section.
VOLATILE_FIELDS = {
    2: "block light",
    3: "light saturation",
    5: "entity count",
    6: "entities",
    9: "mod data",
    10: "light positions",
    11: "server mod data",
    12: "created game version",
    13: "empty flag",
    15: "compression version",
    17: "blocks placed",
    18: "blocks removed",
}


def digest(data: bytes | None) -> bytes:
    if data is None:
        return b"\x00" * 8
    return hashlib.blake2b(data, digest_size=8).digest()


def scan_table(conn: sqlite3.Connection, table: str, wsc) -> dict[int, bytes]:
    """position (unsigned) -> content digest, dimension zero only."""
    out: dict[int, bytes] = {}
    cur = conn.execute(f"SELECT position, data FROM {table}")
    while True:
        batch = cur.fetchmany(4000)
        if not batch:
            break
        for position, data in batch:
            key = position & MASK64
            _, _, _, dimension = wsc.from_index(key)
            if dimension:
                continue
            out[key] = digest(data)
    return out


def column_of(key: int, wsc) -> tuple[int, int]:
    x, _, z, _ = wsc.from_index(key)
    return x, z


def fetch_rows(conn: sqlite3.Connection, table: str, keys: list[int],
               wsc) -> dict[int, bytes]:
    out: dict[int, bytes] = {}
    for start in range(0, len(keys), 400):
        window = keys[start:start + 400]
        signed = [wsc.sqlite_int64(k) for k in window]
        marks = ",".join("?" * len(signed))
        for position, data in conn.execute(
            f"SELECT position, data FROM {table} WHERE position IN ({marks})", signed
        ):
            out[position & MASK64] = data
    return out


def main(argv: list[str]) -> int:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("before", type=Path)
    parser.add_argument("after", type=Path)
    parser.add_argument("--json", type=Path)
    parser.add_argument("--examples", type=int, default=12)
    parser.add_argument("--limit-columns", type=int, default=0,
                        help="attribute at most N changed columns (0 = all)")
    parser.add_argument("--comparator", type=Path)
    args = parser.parse_args(argv)

    wsc = load_comparator(args.comparator)

    started = time.monotonic()
    before = wsc.open_save(args.before)
    after = wsc.open_save(args.after)
    head_a = wsc.read_header(before)
    head_b = wsc.read_header(after)
    identity = {
        "before": {"save_id": head_a.save_id, "seed": head_a.seed,
                   "map": [head_a.map_size_x, head_a.map_size_y, head_a.map_size_z]},
        "after": {"save_id": head_b.save_id, "seed": head_b.seed,
                  "map": [head_b.map_size_x, head_b.map_size_y, head_b.map_size_z]},
        "same_world": (head_a.save_id == head_b.save_id and head_a.seed == head_b.seed
                       and head_a.map_size_x == head_b.map_size_x
                       and head_a.map_size_y == head_b.map_size_y
                       and head_a.map_size_z == head_b.map_size_z),
    }
    print(f"world {head_a.save_id} seed {head_a.seed} "
          f"{'(same world)' if identity['same_world'] else '(DIFFERENT WORLDS)'}",
          flush=True)

    print("reading chunk rows ...", flush=True)
    chunks_a = scan_table(before, "chunk", wsc)
    print(f"  before: {len(chunks_a)} rows ({time.monotonic()-started:.0f}s)", flush=True)
    chunks_b = scan_table(after, "chunk", wsc)
    print(f"  after:  {len(chunks_b)} rows ({time.monotonic()-started:.0f}s)", flush=True)
    maps_a = scan_table(before, "mapchunk", wsc)
    maps_b = scan_table(after, "mapchunk", wsc)
    print(f"  mapchunk: {len(maps_a)} / {len(maps_b)} "
          f"({time.monotonic()-started:.0f}s)", flush=True)

    # Group chunk rows into columns.
    cols_a: dict[tuple[int, int], dict[int, bytes]] = collections.defaultdict(dict)
    for key, dig in chunks_a.items():
        cols_a[column_of(key, wsc)][key] = dig
    cols_b: dict[tuple[int, int], dict[int, bytes]] = collections.defaultdict(dict)
    for key, dig in chunks_b.items():
        cols_b[column_of(key, wsc)][key] = dig

    shared = cols_a.keys() & cols_b.keys()
    only_before = cols_a.keys() - cols_b.keys()
    only_after = cols_b.keys() - cols_a.keys()

    changed: list[tuple[int, int]] = []
    row_added: set[tuple[int, int]] = set()
    row_removed: set[tuple[int, int]] = set()
    for column in shared:
        rows_a, rows_b = cols_a[column], cols_b[column]
        if rows_a == rows_b:
            continue
        changed.append(column)
        if rows_b.keys() - rows_a.keys():
            row_added.add(column)
        if rows_a.keys() - rows_b.keys():
            row_removed.add(column)
    changed.sort()

    print(f"shared columns {len(shared)}, changed {len(changed)} "
          f"({100.0*len(changed)/max(1,len(shared)):.2f}%), "
          f"only-before {len(only_before)}, only-after {len(only_after)}", flush=True)

    sample = changed
    if args.limit_columns and len(changed) > args.limit_columns:
        sample = random.Random(20260920).sample(changed, args.limit_columns)
        sample.sort()
        print(f"attributing a deterministic sample of {len(sample)} columns", flush=True)

    # Attribution pass: decode only the rows that actually differ.
    field_rows = collections.Counter()      # field -> differing rows
    field_columns = collections.Counter()   # field -> columns touching it
    be_tokens = collections.Counter()       # block-entity class -> changed entries
    be_columns = collections.Counter()      # block-entity class -> columns
    classes = collections.Counter()
    class_fields = collections.defaultdict(collections.Counter)
    unreadable = 0
    examples: list[dict] = []
    class_of_column: dict[tuple[int, int], str] = {}

    done = 0
    for column in sample:
        rows_a, rows_b = cols_a[column], cols_b[column]
        differing = [k for k in rows_a.keys() & rows_b.keys() if rows_a[k] != rows_b[k]]
        keys = sorted(differing)
        blobs_a = fetch_rows(before, "chunk", keys, wsc) if keys else {}
        blobs_b = fetch_rows(after, "chunk", keys, wsc) if keys else {}

        fields_here: set[int] = set()
        tokens_here: set[str] = set()
        for key in keys:
            try:
                fields = wsc.differing_fields("chunk", blobs_a[key], blobs_b[key])
            except Exception:
                unreadable += 1
                continue
            for field in fields:
                field_rows[field] += 1
            fields_here.update(fields)
            if 8 in fields:
                try:
                    for entry in changed_block_entities(blobs_a[key], blobs_b[key], wsc):
                        token = be_token(entry)
                        be_tokens[token] += 1
                        tokens_here.add(token)
                except Exception:
                    tokens_here.add("undecodable")
        for token in tokens_here:
            be_columns[token] += 1

        for field in fields_here:
            field_columns[field] += 1

        structural = column in row_added or column in row_removed
        capture = bool(fields_here & CAPTURE_FIELDS.keys())
        entity = bool(fields_here & BLOCK_ENTITY_FIELDS.keys())
        chiselled = bool(tokens_here & CAPTURE_BLOCK_ENTITIES)
        if structural:
            label = "layer added or removed"
        elif capture:
            label = "terrain bytes changed"
        elif entity and chiselled:
            label = "chiselled contents only"
        elif entity:
            label = "other block entities only"
        elif fields_here:
            label = "volatile only"
        else:
            label = "no decodable difference"
        classes[label] += 1
        class_of_column[column] = label
        for field in fields_here:
            class_fields[label][field] += 1

        if len(examples) < args.examples and label in (
                "volatile only", "other block entities only", "chiselled contents only"):
            examples.append({
                "column": list(column),
                "class": label,
                "rows_differing": len(keys),
                "fields": sorted(fields_here),
                "field_names": [wsc.FIELD_NAMES["chunk"].get(f, str(f))
                                for f in sorted(fields_here)],
                "block_entities": sorted(tokens_here),
            })

        done += 1
        if done % 2000 == 0:
            print(f"  attributed {done}/{len(sample)} "
                  f"({time.monotonic()-started:.0f}s)", flush=True)

    # Aside: the fingerprint covers `chunk` only, so a column whose only change is in
    # `mapchunk` (snow accumulation, height maps) is invisible to it.
    map_changed = {column_of(k, wsc) for k in maps_a.keys() & maps_b.keys()
                   if maps_a[k] != maps_b[k]}
    chunk_changed = set(changed)
    map_only = map_changed - chunk_changed
    map_only_fields = collections.Counter()
    map_only_sorted = sorted(map_only)
    map_only_sample = (random.Random(20260920).sample(map_only_sorted, 2000)
                       if len(map_only_sorted) > 2000 else map_only_sorted)
    for column in map_only_sample:
        key = wsc.to_index(column[0], 0, column[1])
        blob_a = fetch_rows(before, "mapchunk", [key], wsc).get(key)
        blob_b = fetch_rows(after, "mapchunk", [key], wsc).get(key)
        if blob_a is None or blob_b is None:
            continue
        try:
            for field in wsc.differing_fields("mapchunk", blob_a, blob_b):
                map_only_fields[field] += 1
        except Exception:
            continue

    report = {
        "before": str(args.before),
        "after": str(args.after),
        "identity": identity,
        "rows": {"before": len(chunks_a), "after": len(chunks_b)},
        "columns": {
            "before": len(cols_a), "after": len(cols_b), "shared": len(shared),
            "only_before": len(only_before), "only_after": len(only_after),
            "changed": len(changed), "attributed": len(sample),
        },
        "classes": dict(classes),
        "class_fields": {k: {f"{f} {wsc.FIELD_NAMES['chunk'].get(f,'?')}": n
                             for f, n in sorted(v.items())}
                         for k, v in class_fields.items()},
        "field_rows": {f"{f} {wsc.FIELD_NAMES['chunk'].get(f, '?')}": n
                       for f, n in sorted(field_rows.items())},
        "field_columns": {f"{f} {wsc.FIELD_NAMES['chunk'].get(f, '?')}": n
                          for f, n in sorted(field_columns.items())},
        "unreadable_rows": unreadable,
        "block_entity_entries_changed": dict(be_tokens.most_common()),
        "block_entity_columns": dict(be_columns.most_common()),
        "examples": examples,
        "mapchunk_only": {
            "columns": len(map_only),
            "sampled": len(map_only_sample),
            "fields": {f"{f} {wsc.FIELD_NAMES['mapchunk'].get(f, '?')}": n
                       for f, n in sorted(map_only_fields.items())},
        },
        "elapsed_seconds": round(time.monotonic() - started, 1),
    }

    print()
    print("classification of changed columns:")
    for label, count in classes.most_common():
        print(f"  {label:28s} {count:8d}  "
              f"{100.0*count/max(1,len(sample)):6.2f}%")
    print()
    for label in classes:
        fields = class_fields[label]
        print(f"  [{label}] " + ", ".join(
            f"{wsc.FIELD_NAMES['chunk'].get(f,'?')} {n}" for f, n in sorted(fields.items())))
    print()
    print("fields differing, by column:")
    for field, count in sorted(field_columns.items()):
        print(f"  {field:2d} {wsc.FIELD_NAMES['chunk'].get(field,'?'):22s} {count:8d}")
    print()
    print("block-entity classes whose entries changed (columns):")
    for token, count in be_columns.most_common(15):
        mark = "  <- capture reads this" if token in CAPTURE_BLOCK_ENTITIES else ""
        print(f"  {token:28s} {count:8d}{mark}")
    print()
    print(f"columns whose only change is in mapchunk: {len(map_only)}")
    for field, count in sorted(map_only_fields.items()):
        print(f"  {field:2d} {wsc.FIELD_NAMES['mapchunk'].get(field,'?'):26s} {count:8d}"
              f" (of {len(map_only_sample)} sampled)")

    if args.json:
        args.json.write_text(json.dumps(report, indent=2), encoding="utf-8")
        print(f"\nwrote {args.json}")
    return 0


if __name__ == "__main__":
    raise SystemExit(main(sys.argv[1:]))
