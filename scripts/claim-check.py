#!/usr/bin/env python3
"""How many of a cache's column claims the savegame still agrees with.

A claim in `ColumnFingerprint` says "this cache holds what the savegame held for this column
at that moment". The startup sweep skips a column while its claim matches, and the
background audit disputes one that stops matching. This replays that comparison offline, so
the audit's real rate on a real world can be measured without launching anything.

It does NOT reimplement the mod's hash. It reads a dump produced by
`scripts/fingerprint-dump`, which runs `LodSavegameScan` itself, so the two can never drift -
an earlier version of this tool carried its own copy of the rule and silently reported every
claim as stale the day the rule changed (Session 105).

    dotnet run --project scripts/fingerprint-dump -- WORLD.vcdbs dump.tsv
    claim-check.py dump.tsv CACHE-server.db [--json report.json] [--by-minute]

`--by-minute` splits the result by when each claim was written, which is what separates
claims made under one rule or code path from another within a single session.

Read-only. Safe to run while the game is open, but a claim written after the world's last
save will read as stale simply because the file does not hold it yet; quit first for a
clean number.
"""

from __future__ import annotations

import argparse
import collections
import datetime
import json
import sqlite3
import sys
from pathlib import Path

MASK64 = (1 << 64) - 1


def read_dump(path: Path) -> dict[tuple[int, int], int]:
    """cx, cz -> sealed fingerprint, as `fingerprint-dump` writes it."""
    out: dict[tuple[int, int], int] = {}
    with path.open(encoding="utf-8") as handle:
        for line in handle:
            parts = line.split()
            if len(parts) != 4:
                continue
            cx, cz, _rows, seal = parts
            out[(int(cx), int(cz))] = int(seal)
    return out


def main(argv: list[str]) -> int:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("dump", type=Path, help="TSV from scripts/fingerprint-dump")
    parser.add_argument("cache", type=Path, help="<worldid>-server.db")
    parser.add_argument("--json", type=Path)
    parser.add_argument("--by-minute", action="store_true")
    args = parser.parse_args(argv)

    savegame = read_dump(args.dump)
    if not savegame:
        raise SystemExit(f"no fingerprints in {args.dump}")

    conn = sqlite3.connect("file:" + args.cache.resolve().as_posix() + "?mode=ro", uri=True)
    try:
        rows = conn.execute(
            "SELECT CX, CZ, Fingerprint, ModifiedMs FROM ColumnFingerprint").fetchall()
    except sqlite3.OperationalError as error:
        raise SystemExit(f"{args.cache} holds no column claims: {error}")

    agree = disagree = absent = 0
    per_minute: collections.Counter = collections.Counter()
    stale_per_minute: collections.Counter = collections.Counter()
    stale: list[dict] = []

    for cx, cz, stored, modified in rows:
        minute = datetime.datetime.fromtimestamp(modified / 1000).strftime("%Y-%m-%d %H:%M")
        per_minute[minute] += 1
        current = savegame.get((cx, cz))
        if current is None:
            absent += 1
            continue
        if current == (stored & MASK64):
            agree += 1
            continue
        disagree += 1
        stale_per_minute[minute] += 1
        if len(stale) < 200:
            stale.append({"column": [cx, cz], "claimed": minute})

    total = len(rows)
    print(f"claims {total}")
    print(f"  the savegame still agrees with   {agree:7d}  {100.0*agree/max(1,total):6.2f}%")
    print(f"  the audit would dispute now      {disagree:7d}  {100.0*disagree/max(1,total):6.2f}%")
    if absent:
        print(f"  columns the savegame no longer holds {absent}")
    if agree == 0 and total:
        print("WARNING: nothing matched. Check that the dump came from the same world, and "
              "that it was produced by a build whose fingerprint rule matches the cache's "
              "stored Meta.FingerprintRule.")

    if args.by_minute:
        print()
        print(f"{'claim written':18s} {'claims':>7s} {'stale':>7s} {'rate':>7s}")
        for minute in sorted(per_minute):
            claims = per_minute[minute]
            bad = stale_per_minute[minute]
            print(f"{minute:18s} {claims:7d} {bad:7d} {100.0*bad/claims:6.2f}%")

    if args.json:
        args.json.write_text(json.dumps({
            "dump": str(args.dump),
            "cache": str(args.cache),
            "claims": total,
            "agree": agree,
            "disagree": disagree,
            "absent_from_savegame": absent,
            "by_minute": {k: {"claims": per_minute[k], "stale": stale_per_minute[k]}
                          for k in sorted(per_minute)},
            "stale_examples": stale,
        }, indent=2), encoding="utf-8")
        print(f"\nwrote {args.json}")
    return 0


if __name__ == "__main__":
    raise SystemExit(main(sys.argv[1:]))
