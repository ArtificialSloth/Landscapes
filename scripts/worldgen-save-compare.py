#!/usr/bin/env python3
"""Compare the terrain produced by two Vintage Story world-generation runs.

The P0 thread-safety gate needs more than a whole-file hash.  Savegame headers contain
clock and play-time fields, SQLite page layout depends on write order, and protobuf map
entries may be serialized in a different order while meaning the same thing.  This tool
opens both settled saves read-only, restricts itself to the `/wgen pregen` square around
world centre, decodes every terrain row into protobuf fields, and compares a canonical
field representation.

Usage:
    worldgen-save-compare.py inspect SAVE [--range 30]
    worldgen-save-compare.py compare THREADS_1 THREADS_6 [--range 30]
                                      [--json REPORT.json]
    worldgen-save-compare.py triage CONTROL_1A CONTROL_1B THREADS_6 [--range 30]
                                      [--json REPORT.json]
    worldgen-save-compare.py selftest

Exit status for ``compare`` is 0 only when both saves are the same world, both contain
the complete requested map-column square, their terrain key sets match, and every
decoded row is semantically identical.  Representation-only differences are reported
but do not fail the gate.  A non-empty SQLite WAL is always refused: the input is not a
settled snapshot yet.
"""

from __future__ import annotations

import argparse
import collections
import contextlib
import dataclasses
import hashlib
import json
import os
from pathlib import Path
import sqlite3
import struct
import sys
import tempfile
from typing import Iterable, Iterator


CHUNK_MASK = 0x3FFFFF
Y_MASK = 0x1FF
DIM_LOW_MASK = 0x1F
DIM_HIGH_MASK = 0x3E0


FIELD_NAMES: dict[str, dict[int, str]] = {
    "chunk": {
        1: "blocks",
        2: "block light",
        3: "light saturation",
        5: "entity count",
        6: "entities",
        7: "block-entity count",
        8: "block entities",
        9: "mod data",
        10: "light positions",
        11: "server mod data",
        12: "created game version",
        13: "empty flag",
        14: "decors",
        15: "compression version",
        16: "fluids",
        17: "blocks placed",
        18: "blocks removed",
    },
    "mapchunk": {
        1: "mod data",
        3: "rain height map",
        4: "worldgen pass",
        6: "old top-rock map",
        7: "terrain height map",
        8: "scheduled block updates",
        9: "new block entities",
        10: "maximum height",
        11: "cave distortion",
        12: "sedimentary thickness",
        13: "top-rock map",
        15: "old snow accumulation",
        16: "worldgen version",
        17: "scheduled light updates",
        18: "snow accumulation",
    },
    "mapregion": {
        1: "landforms",
        2: "forest",
        3: "climate",
        4: "geologic provinces",
        5: "bushes",
        6: "biomes",
        8: "ore maps",
        9: "mod data",
        10: "mod maps",
        11: "generated structures",
        12: "rock strata",
        13: "ore top distortion",
        14: "ore bottom distortion",
        15: "beaches",
        16: "block-patch maps",
        17: "upheaval",
        18: "oceans",
        19: "worldgen version",
        20: "created game version",
        21: "animal spawn maps",
        22: "low-resolution height map",
        23: "rivers",
    },
}

# These fields originate in dictionaries, sets, or a block-entity dictionary.  Their
# protobuf occurrence order is not part of the saved world's meaning, so canonicalize it.
# Lists that can affect later behavior (entities, scheduled updates, structures) stay
# ordered and a different order is deliberately reported as a semantic mismatch.
UNORDERED_FIELDS: dict[str, set[int]] = {
    "chunk": {8, 9, 10, 11},
    "mapchunk": {1},
    "mapregion": {8, 9, 10, 16, 21},
}


class CompareError(RuntimeError):
    pass


@dataclasses.dataclass(frozen=True)
class WorldHeader:
    map_size_x: int
    map_size_y: int
    map_size_z: int
    seed: int
    save_id: str

    @property
    def centre_chunk_x(self) -> int:
        return self.map_size_x // 32 // 2

    @property
    def centre_chunk_z(self) -> int:
        return self.map_size_z // 32 // 2

    @property
    def chunks_y(self) -> int:
        return (self.map_size_y + 31) // 32


@dataclasses.dataclass
class TableStats:
    table: str
    left_rows: int = 0
    right_rows: int = 0
    shared_rows: int = 0
    only_left: int = 0
    only_right: int = 0
    raw_mismatches: int = 0
    representation_only: int = 0
    semantic_mismatches: int = 0
    unreadable_rows: int = 0
    field_mismatches: collections.Counter[int] = dataclasses.field(
        default_factory=collections.Counter
    )
    examples: list[dict[str, object]] = dataclasses.field(default_factory=list)
    left_hash: str = ""
    right_hash: str = ""

    def key_sets_match(self) -> bool:
        return self.only_left == 0 and self.only_right == 0

    def semantics_match(self) -> bool:
        return self.key_sets_match() and self.semantic_mismatches == 0

    def as_json(self) -> dict[str, object]:
        result = dataclasses.asdict(self)
        result["field_mismatches"] = {
            str(k): v for k, v in sorted(self.field_mismatches.items())
        }
        return result


@dataclasses.dataclass
class TriageStats:
    table: str
    shared_rows: int = 0
    missing_rows: int = 0
    all_equal: int = 0
    controls_equal_six_differs: int = 0
    control_a_six_equal: int = 0
    control_b_six_equal: int = 0
    all_different: int = 0
    unreadable_rows: int = 0
    controls_equal_field_mismatches: collections.Counter[int] = dataclasses.field(
        default_factory=collections.Counter
    )
    examples: list[dict[str, object]] = dataclasses.field(default_factory=list)

    def as_json(self) -> dict[str, object]:
        result = dataclasses.asdict(self)
        result["controls_equal_field_mismatches"] = {
            str(k): v for k, v in sorted(self.controls_equal_field_mismatches.items())
        }
        return result


def read_varint(data: bytes, offset: int) -> tuple[int, int]:
    value = 0
    shift = 0
    while offset < len(data) and shift < 70:
        byte = data[offset]
        offset += 1
        value |= (byte & 0x7F) << shift
        if byte < 0x80:
            return value, offset
        shift += 7
    raise CompareError("truncated or overlong protobuf varint")


def encode_varint(value: int) -> bytes:
    value &= (1 << 64) - 1
    out = bytearray()
    while value >= 0x80:
        out.append((value & 0x7F) | 0x80)
        value >>= 7
    out.append(value)
    return bytes(out)


WireValue = tuple[int, int | bytes]


def parse_fields(data: bytes) -> dict[int, list[WireValue]]:
    fields: dict[int, list[WireValue]] = collections.defaultdict(list)
    offset = 0
    while offset < len(data):
        tag, offset = read_varint(data, offset)
        number, wire = tag >> 3, tag & 7
        if number == 0:
            raise CompareError("protobuf field zero is invalid")
        if wire == 0:
            value, offset = read_varint(data, offset)
        elif wire == 1:
            if offset + 8 > len(data):
                raise CompareError("truncated fixed64 field")
            value = data[offset : offset + 8]
            offset += 8
        elif wire == 2:
            length, offset = read_varint(data, offset)
            if length > len(data) - offset:
                raise CompareError("truncated length-delimited field")
            value = data[offset : offset + length]
            offset += length
        elif wire == 5:
            if offset + 4 > len(data):
                raise CompareError("truncated fixed32 field")
            value = data[offset : offset + 4]
            offset += 4
        else:
            raise CompareError(f"unsupported protobuf wire type {wire}")
        fields[number].append((wire, value))
    return dict(fields)


def wire_bytes(value: WireValue) -> bytes:
    wire, payload = value
    if wire == 0:
        assert isinstance(payload, int)
        return encode_varint(payload)
    assert isinstance(payload, bytes)
    if wire == 2:
        return encode_varint(len(payload)) + payload
    return payload


def normalized_values(table: str, field: int, values: list[WireValue]) -> list[WireValue]:
    if field not in UNORDERED_FIELDS[table]:
        return values
    return sorted(values, key=lambda item: bytes([item[0]]) + wire_bytes(item))


def canonical_blob(table: str, data: bytes) -> bytes:
    fields = parse_fields(data)
    out = bytearray()
    for field in sorted(fields):
        for value in normalized_values(table, field, fields[field]):
            wire = value[0]
            out += encode_varint((field << 3) | wire)
            out += wire_bytes(value)
    return bytes(out)


def differing_fields(table: str, left: bytes, right: bytes) -> list[int]:
    left_fields = parse_fields(left)
    right_fields = parse_fields(right)
    changed: list[int] = []
    for field in sorted(left_fields.keys() | right_fields.keys()):
        a = normalized_values(table, field, left_fields.get(field, []))
        b = normalized_values(table, field, right_fields.get(field, []))
        if a != b:
            changed.append(field)
    return changed


def first_varint(fields: dict[int, list[WireValue]], number: int) -> int:
    try:
        wire, value = fields[number][0]
    except (KeyError, IndexError) as exc:
        raise CompareError(f"save header is missing protobuf field {number}") from exc
    if wire != 0 or not isinstance(value, int):
        raise CompareError(f"save header field {number} is not a varint")
    return value


def first_text(fields: dict[int, list[WireValue]], number: int) -> str:
    try:
        wire, value = fields[number][0]
    except (KeyError, IndexError) as exc:
        raise CompareError(f"save header is missing protobuf field {number}") from exc
    if wire != 2 or not isinstance(value, bytes):
        raise CompareError(f"save header field {number} is not text")
    return value.decode("utf-8", errors="replace")


def signed_int32(value: int) -> int:
    value &= 0xFFFFFFFF
    return value - (1 << 32) if value & 0x80000000 else value


def open_save(path: Path) -> sqlite3.Connection:
    if not path.is_file():
        raise CompareError(f"save does not exist: {path}")
    wal = Path(str(path) + "-wal")
    if wal.exists() and wal.stat().st_size:
        raise CompareError(f"save has a non-empty SQLite WAL; stop it cleanly first: {wal}")
    uri = "file:" + path.resolve().as_posix() + "?mode=ro"
    connection = sqlite3.connect(uri, uri=True)
    connection.execute("PRAGMA query_only=ON")
    tables = {
        row[0]
        for row in connection.execute(
            "SELECT name FROM sqlite_master WHERE type='table'"
        )
    }
    required = {"gamedata", "mapchunk", "chunk", "mapregion"}
    if not required.issubset(tables):
        connection.close()
        raise CompareError(f"not a supported Vintage Story save: {path}")
    return connection


def read_header(connection: sqlite3.Connection) -> WorldHeader:
    row = connection.execute("SELECT data FROM gamedata LIMIT 1").fetchone()
    if row is None or not isinstance(row[0], bytes):
        raise CompareError("save has no readable gamedata row")
    fields = parse_fields(row[0])
    return WorldHeader(
        map_size_x=first_varint(fields, 1),
        map_size_y=first_varint(fields, 2),
        map_size_z=first_varint(fields, 3),
        seed=signed_int32(first_varint(fields, 7)),
        save_id=first_text(fields, 34),
    )


def to_index(x: int, y: int, z: int, dimension: int = 0) -> int:
    if min(x, y, z, dimension) < 0:
        raise CompareError("the P0 comparator only supports dimension-zero positive coordinates")
    value = (x & CHUNK_MASK) | ((z & CHUNK_MASK) << 27) | ((y & Y_MASK) << 54)
    if dimension:
        value |= (dimension & DIM_LOW_MASK) << 22
        value |= (dimension & DIM_HIGH_MASK) << 44
    return value


def from_index(position: int) -> tuple[int, int, int, int]:
    value = position & ((1 << 64) - 1)
    x = value & CHUNK_MASK
    y = (value >> 54) & Y_MASK
    z = (value >> 27) & CHUNK_MASK
    dimension = ((value >> 22) & DIM_LOW_MASK) + ((value >> 44) & DIM_HIGH_MASK)
    return x, y, z, dimension


def sqlite_int64(value: int) -> int:
    return value - (1 << 64) if value & (1 << 63) else value


def rows_in_x_range(
    connection: sqlite3.Connection,
    table: str,
    x1: int,
    x2: int,
    y: int,
    z: int,
) -> dict[int, bytes]:
    low = sqlite_int64(to_index(x1, y, z))
    high = sqlite_int64(to_index(x2, y, z))
    rows: dict[int, bytes] = {}
    for position, data in connection.execute(
        f"SELECT position, data FROM {table} "
        "WHERE position >= ? AND position <= ? ORDER BY position",
        (low, high),
    ):
        if isinstance(data, bytes):
            rows[int(position)] = data
    return rows


def ranges_for(table: str, header: WorldHeader, radius: int) -> Iterator[tuple[int, int, int, int]]:
    x1 = max(0, header.centre_chunk_x - radius)
    x2 = min(header.map_size_x // 32 - 1, header.centre_chunk_x + radius)
    z1 = max(0, header.centre_chunk_z - radius)
    z2 = min(header.map_size_z // 32 - 1, header.centre_chunk_z + radius)
    if table == "chunk":
        for y in range(header.chunks_y):
            for z in range(z1, z2 + 1):
                yield x1, x2, y, z
    elif table == "mapchunk":
        for z in range(z1, z2 + 1):
            yield x1, x2, 0, z
    elif table == "mapregion":
        region_x1, region_x2 = x1 // 16, x2 // 16
        for region_z in range(z1 // 16, z2 // 16 + 1):
            yield region_x1, region_x2, 0, region_z
    else:
        raise CompareError(f"unknown terrain table: {table}")


def target_mapchunk_count(header: WorldHeader, radius: int) -> int:
    x1 = max(0, header.centre_chunk_x - radius)
    x2 = min(header.map_size_x // 32 - 1, header.centre_chunk_x + radius)
    z1 = max(0, header.centre_chunk_z - radius)
    z2 = min(header.map_size_z // 32 - 1, header.centre_chunk_z + radius)
    return max(0, x2 - x1 + 1) * max(0, z2 - z1 + 1)


def describe_position(table: str, position: int) -> str:
    x, y, z, dimension = from_index(position)
    if table == "mapregion":
        return f"region ({x},{z}, dimension {dimension})"
    if table == "mapchunk":
        return f"column ({x},{z}, dimension {dimension})"
    return f"chunk ({x},{y},{z}, dimension {dimension})"


def compare_table(
    left: sqlite3.Connection,
    right: sqlite3.Connection,
    table: str,
    header: WorldHeader,
    radius: int,
) -> TableStats:
    stats = TableStats(table=table)
    left_hash = hashlib.sha256()
    right_hash = hashlib.sha256()

    for x1, x2, y, z in ranges_for(table, header, radius):
        left_rows = rows_in_x_range(left, table, x1, x2, y, z)
        right_rows = rows_in_x_range(right, table, x1, x2, y, z)
        stats.left_rows += len(left_rows)
        stats.right_rows += len(right_rows)
        for position in sorted(left_rows.keys() | right_rows.keys()):
            a = left_rows.get(position)
            b = right_rows.get(position)
            position_bytes = struct.pack("<Q", position & ((1 << 64) - 1))
            if a is None:
                stats.only_right += 1
                right_hash.update(position_bytes + canonical_blob(table, b))
                if len(stats.examples) < 12:
                    stats.examples.append(
                        {"position": describe_position(table, position), "difference": "only in threads-6"}
                    )
                continue
            if b is None:
                stats.only_left += 1
                left_hash.update(position_bytes + canonical_blob(table, a))
                if len(stats.examples) < 12:
                    stats.examples.append(
                        {"position": describe_position(table, position), "difference": "only in threads-1"}
                    )
                continue

            stats.shared_rows += 1
            try:
                canonical_a = canonical_blob(table, a)
                canonical_b = canonical_blob(table, b)
                left_hash.update(position_bytes + canonical_a)
                right_hash.update(position_bytes + canonical_b)
                if a == b:
                    continue
                stats.raw_mismatches += 1
                fields = differing_fields(table, a, b)
                if not fields:
                    stats.representation_only += 1
                    continue
                stats.semantic_mismatches += 1
                stats.field_mismatches.update(fields)
                if len(stats.examples) < 12:
                    names = [FIELD_NAMES[table].get(field, f"unknown field {field}") for field in fields]
                    stats.examples.append(
                        {
                            "position": describe_position(table, position),
                            "difference": ", ".join(names),
                            "fields": fields,
                        }
                    )
            except CompareError as exc:
                stats.unreadable_rows += 1
                stats.semantic_mismatches += 1
                left_hash.update(position_bytes + a)
                right_hash.update(position_bytes + b)
                if len(stats.examples) < 12:
                    stats.examples.append(
                        {"position": describe_position(table, position), "difference": f"decode error: {exc}"}
                    )

    stats.left_hash = left_hash.hexdigest()
    stats.right_hash = right_hash.hexdigest()
    return stats


def header_identity(header: WorldHeader) -> tuple[int, int, int, int, str]:
    return (
        header.map_size_x,
        header.map_size_y,
        header.map_size_z,
        header.seed,
        header.save_id,
    )


def compare_saves(left_path: Path, right_path: Path, radius: int) -> dict[str, object]:
    with contextlib.closing(open_save(left_path)) as left, contextlib.closing(open_save(right_path)) as right:
        left_header = read_header(left)
        right_header = read_header(right)
        same_world = header_identity(left_header) == header_identity(right_header)
        if not same_world:
            return {
                "passed": False,
                "same_world": False,
                "left_header": dataclasses.asdict(left_header),
                "right_header": dataclasses.asdict(right_header),
                "range": radius,
                "expected_mapchunks": None,
                "tables": [],
            }

        tables = [
            compare_table(left, right, table, left_header, radius)
            for table in ("mapchunk", "chunk", "mapregion")
        ]
        expected = target_mapchunk_count(left_header, radius)
        mapchunks = tables[0]
        complete = mapchunks.left_rows == expected and mapchunks.right_rows == expected
        passed = complete and all(table.semantics_match() for table in tables)
        return {
            "passed": passed,
            "same_world": True,
            "left_header": dataclasses.asdict(left_header),
            "right_header": dataclasses.asdict(right_header),
            "range": radius,
            "expected_mapchunks": expected,
            "complete_mapchunk_square": complete,
            "tables": [table.as_json() for table in tables],
        }


def triage_table(
    control_a: sqlite3.Connection,
    control_b: sqlite3.Connection,
    six: sqlite3.Connection,
    table: str,
    header: WorldHeader,
    radius: int,
) -> TriageStats:
    stats = TriageStats(table=table)
    for x1, x2, y, z in ranges_for(table, header, radius):
        rows_a = rows_in_x_range(control_a, table, x1, x2, y, z)
        rows_b = rows_in_x_range(control_b, table, x1, x2, y, z)
        rows_six = rows_in_x_range(six, table, x1, x2, y, z)
        for position in sorted(rows_a.keys() | rows_b.keys() | rows_six.keys()):
            a = rows_a.get(position)
            b = rows_b.get(position)
            c = rows_six.get(position)
            if a is None or b is None or c is None:
                stats.missing_rows += 1
                continue
            stats.shared_rows += 1
            try:
                canonical_a = canonical_blob(table, a)
                canonical_b = canonical_blob(table, b)
                canonical_c = canonical_blob(table, c)
                if canonical_a == canonical_b == canonical_c:
                    stats.all_equal += 1
                elif canonical_a == canonical_b:
                    stats.controls_equal_six_differs += 1
                    fields = differing_fields(table, a, c)
                    stats.controls_equal_field_mismatches.update(fields)
                    if len(stats.examples) < 12:
                        names = [
                            FIELD_NAMES[table].get(field, f"unknown field {field}")
                            for field in fields
                        ]
                        stats.examples.append(
                            {
                                "position": describe_position(table, position),
                                "difference": ", ".join(names),
                                "fields": fields,
                            }
                        )
                elif canonical_a == canonical_c:
                    stats.control_a_six_equal += 1
                elif canonical_b == canonical_c:
                    stats.control_b_six_equal += 1
                else:
                    stats.all_different += 1
            except CompareError:
                stats.unreadable_rows += 1
    return stats


def triage_saves(
    control_a_path: Path,
    control_b_path: Path,
    six_path: Path,
    radius: int,
) -> dict[str, object]:
    with (
        contextlib.closing(open_save(control_a_path)) as control_a,
        contextlib.closing(open_save(control_b_path)) as control_b,
        contextlib.closing(open_save(six_path)) as six,
    ):
        headers = [read_header(connection) for connection in (control_a, control_b, six)]
        same_world = len({header_identity(header) for header in headers}) == 1
        if not same_world:
            return {
                "same_world": False,
                "headers": [dataclasses.asdict(header) for header in headers],
                "range": radius,
                "expected_mapchunks": None,
                "tables": [],
            }
        tables = [
            triage_table(control_a, control_b, six, table, headers[0], radius)
            for table in ("mapchunk", "chunk", "mapregion")
        ]
        return {
            "same_world": True,
            "headers": [dataclasses.asdict(header) for header in headers],
            "range": radius,
            "expected_mapchunks": target_mapchunk_count(headers[0], radius),
            "tables": [table.as_json() for table in tables],
        }


def inspect_save(path: Path, radius: int) -> dict[str, object]:
    with contextlib.closing(open_save(path)) as connection:
        header = read_header(connection)
        target: dict[str, int] = {}
        total: dict[str, int] = {}
        for table in ("mapchunk", "chunk", "mapregion"):
            count = 0
            for x1, x2, y, z in ranges_for(table, header, radius):
                count += len(rows_in_x_range(connection, table, x1, x2, y, z))
            target[table] = count
            total[table] = int(connection.execute(f"SELECT count(*) FROM {table}").fetchone()[0])
        return {
            "path": str(path.resolve()),
            "bytes": path.stat().st_size,
            "header": dataclasses.asdict(header),
            "centre_chunk": [header.centre_chunk_x, header.centre_chunk_z],
            "range": radius,
            "expected_mapchunks": target_mapchunk_count(header, radius),
            "target_rows": target,
            "total_rows": total,
        }


def print_inspection(report: dict[str, object]) -> None:
    header = report["header"]
    assert isinstance(header, dict)
    print()
    print(f"  save: {report['path']}")
    print(f"  size: {int(report['bytes']) / (1024 ** 3):.2f} GiB")
    print(
        "  world: seed {seed}, map {map_size_x} x {map_size_y} x {map_size_z}, id {save_id}".format(
            **header
        )
    )
    centre = report["centre_chunk"]
    print(f"  /wgen pregen centre: chunk {centre[0]},{centre[1]}; radius {report['range']}")
    print(f"  requested square: {report['expected_mapchunks']} map columns")
    print()
    print("  table        target rows    whole save")
    for table in ("mapchunk", "chunk", "mapregion"):
        print(f"  {table:<11} {report['target_rows'][table]:>11} {report['total_rows'][table]:>13}")
    print()


def print_comparison(report: dict[str, object], left: Path, right: Path) -> None:
    print()
    print("  Vintage Horizons multi-thread worldgen gate")
    print(f"  threads 1: {left.resolve()}")
    print(f"  threads 6: {right.resolve()}")
    print(f"  radius: {report['range']} chunks around world centre")
    print()
    if not report["same_world"]:
        print("  FAIL: the two files are not copies of the same seed world.")
        return

    header = report["left_header"]
    print(
        f"  seed {header['seed']}, map {header['map_size_x']} x {header['map_size_y']} x "
        f"{header['map_size_z']}, id {header['save_id']}"
    )
    print(f"  expected map columns: {report['expected_mapchunks']}")
    print()
    print("  table          rows 1   rows 6   only 1   only 6   raw diff   semantic diff")
    for table in report["tables"]:
        print(
            f"  {table['table']:<11} {table['left_rows']:>8} {table['right_rows']:>8} "
            f"{table['only_left']:>8} {table['only_right']:>8} "
            f"{table['raw_mismatches']:>10} {table['semantic_mismatches']:>15}"
        )
        if table["representation_only"]:
            print(
                f"      {table['representation_only']} raw differences canonicalize to the same "
                "dictionary/set contents"
            )
        if table["field_mismatches"]:
            details = []
            for field_text, count in table["field_mismatches"].items():
                field = int(field_text)
                name = FIELD_NAMES[table["table"]].get(field, f"unknown field {field}")
                details.append(f"{name} ({count})")
            print("      changed fields: " + ", ".join(details))
        for example in table["examples"]:
            print(f"      - {example['position']}: {example['difference']}")
        print(f"      canonical hashes: {table['left_hash'][:16]}  {table['right_hash'][:16]}")
    print()
    if report["passed"]:
        print("  PASS: the complete generated square is semantically identical at 1 and 6 threads.")
    elif not report.get("complete_mapchunk_square", False):
        print("  FAIL: one or both saves do not contain the complete requested map-column square.")
    else:
        print("  FAIL: generated terrain differs; the Experimental thread option stays gated.")
    print()


def print_triage(report: dict[str, object]) -> None:
    print()
    print("  Three-way worldgen attribution")
    print(f"  radius: {report['range']} chunks around world centre")
    print()
    if not report["same_world"]:
        print("  FAIL: the three files are not copies of the same seed world.")
        return
    print(
        "  table          shared  all equal  controls=same,6!=  "
        "A=6  B=6  all different  missing"
    )
    for table in report["tables"]:
        print(
            f"  {table['table']:<11} {table['shared_rows']:>8} {table['all_equal']:>10} "
            f"{table['controls_equal_six_differs']:>17} "
            f"{table['control_a_six_equal']:>4} {table['control_b_six_equal']:>4} "
            f"{table['all_different']:>14} {table['missing_rows']:>8}"
        )
        if table["controls_equal_field_mismatches"]:
            details = []
            for field_text, count in table["controls_equal_field_mismatches"].items():
                field = int(field_text)
                name = FIELD_NAMES[table["table"]].get(field, f"unknown field {field}")
                details.append(f"{name} ({count})")
            print("      fields where controls agree and 6 differs: " + ", ".join(details))
        for example in table["examples"]:
            print(f"      - {example['position']}: {example['difference']}")
    print()


def protobuf_varint(field: int, value: int) -> bytes:
    return encode_varint((field << 3) | 0) + encode_varint(value)


def protobuf_bytes(field: int, value: bytes) -> bytes:
    return encode_varint((field << 3) | 2) + encode_varint(len(value)) + value


def selftest() -> int:
    with tempfile.TemporaryDirectory(prefix="vh-worldgen-compare-") as directory:
        root = Path(directory)
        left = root / "one.vcdbs"
        right = root / "six.vcdbs"
        multi = root / "multi.vcdbs"
        header = (
            protobuf_varint(1, 32)
            + protobuf_varint(2, 32)
            + protobuf_varint(3, 32)
            + protobuf_varint(7, 1234)
            + protobuf_bytes(34, b"same-world")
        )
        dictionary_a = protobuf_bytes(1, b"a") + protobuf_bytes(2, b"1")
        dictionary_b = protobuf_bytes(1, b"b") + protobuf_bytes(2, b"2")
        common_mapchunk = protobuf_varint(4, 6) + protobuf_bytes(7, b"height")
        left_chunk = (
            protobuf_bytes(1, b"blocks")
            + protobuf_bytes(9, dictionary_a)
            + protobuf_bytes(9, dictionary_b)
        )
        right_chunk = (
            protobuf_bytes(9, dictionary_b)
            + protobuf_bytes(1, b"blocks")
            + protobuf_bytes(9, dictionary_a)
        )

        different_chunk = protobuf_bytes(1, b"different-blocks")
        for path, chunk in (
            (left, left_chunk),
            (right, right_chunk),
            (multi, different_chunk),
        ):
            connection = sqlite3.connect(path)
            for table in ("gamedata", "mapchunk", "chunk", "mapregion"):
                key = "savegameid" if table == "gamedata" else "position"
                connection.execute(f"CREATE TABLE {table} ({key} integer PRIMARY KEY, data BLOB)")
            connection.execute("INSERT INTO gamedata VALUES (1, ?)", (header,))
            connection.execute("INSERT INTO mapchunk VALUES (?, ?)", (to_index(0, 0, 0), common_mapchunk))
            connection.execute("INSERT INTO chunk VALUES (?, ?)", (to_index(0, 0, 0), chunk))
            connection.commit()
            connection.close()

        equivalent = compare_saves(left, right, 0)
        if not equivalent["passed"]:
            print("selftest failed: dictionary order should be semantically equivalent")
            return 1
        if equivalent["tables"][1]["representation_only"] != 1:
            print("selftest failed: representation-only difference was not reported")
            return 1

        mismatch = compare_saves(left, multi, 0)
        if mismatch["passed"] or mismatch["tables"][1]["semantic_mismatches"] != 1:
            print("selftest failed: changed block payload did not fail the gate")
            return 1
        triage = triage_saves(left, right, multi, 0)
        chunk_triage = triage["tables"][1]
        if chunk_triage["controls_equal_six_differs"] != 1:
            print("selftest failed: three-way control agreement was not attributed")
            return 1

    print("worldgen-save-compare selftest: ok")
    return 0


def parse_args(argv: list[str]) -> argparse.Namespace:
    parser = argparse.ArgumentParser(description=__doc__)
    subparsers = parser.add_subparsers(dest="command", required=True)
    inspect_parser = subparsers.add_parser("inspect", help="describe the target square in one save")
    inspect_parser.add_argument("save", type=Path)
    inspect_parser.add_argument("--range", type=int, default=30)
    inspect_parser.add_argument("--json", action="store_true", help="print machine-readable JSON")
    compare_parser = subparsers.add_parser("compare", help="compare 1-thread and 6-thread saves")
    compare_parser.add_argument("threads_1", type=Path)
    compare_parser.add_argument("threads_6", type=Path)
    compare_parser.add_argument("--range", type=int, default=30)
    compare_parser.add_argument("--json", type=Path)
    triage_parser = subparsers.add_parser(
        "triage", help="classify two 1-thread controls against one 6-thread save"
    )
    triage_parser.add_argument("control_1a", type=Path)
    triage_parser.add_argument("control_1b", type=Path)
    triage_parser.add_argument("threads_6", type=Path)
    triage_parser.add_argument("--range", type=int, default=30)
    triage_parser.add_argument("--json", type=Path)
    subparsers.add_parser("selftest", help="run the comparator's isolated fixture checks")
    return parser.parse_args(argv)


def main(argv: list[str]) -> int:
    args = parse_args(argv)
    if args.command == "selftest":
        return selftest()
    if args.range < 0 or args.range > 50:
        raise CompareError("range must be between 0 and 50, matching vanilla /wgen limits")
    if args.command == "inspect":
        report = inspect_save(args.save, args.range)
        if args.json:
            print(json.dumps(report))
        else:
            print_inspection(report)
        return 0

    if args.command == "triage":
        report = triage_saves(args.control_1a, args.control_1b, args.threads_6, args.range)
        print_triage(report)
        if args.json:
            args.json.parent.mkdir(parents=True, exist_ok=True)
            temporary = args.json.with_suffix(args.json.suffix + ".tmp")
            temporary.write_text(json.dumps(report, indent=2) + "\n", encoding="utf-8")
            os.replace(temporary, args.json)
        return 0 if report["same_world"] else 1

    report = compare_saves(args.threads_1, args.threads_6, args.range)
    print_comparison(report, args.threads_1, args.threads_6)
    if args.json:
        args.json.parent.mkdir(parents=True, exist_ok=True)
        temporary = args.json.with_suffix(args.json.suffix + ".tmp")
        temporary.write_text(json.dumps(report, indent=2) + "\n", encoding="utf-8")
        os.replace(temporary, args.json)
    return 0 if report["passed"] else 1


if __name__ == "__main__":
    try:
        raise SystemExit(main(sys.argv[1:]))
    except (CompareError, sqlite3.Error, OSError) as exc:
        print(f"worldgen-save-compare: {exc}", file=sys.stderr)
        raise SystemExit(2)
