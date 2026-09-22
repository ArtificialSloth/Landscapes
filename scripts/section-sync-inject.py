"""Inspect or change a SANDBOX section-sync profile's caches between integration runs.

Session 109 tested section sync (terrain-change plan Phase B) with a dedicated sandbox server and
client, run several times through scripts/bench-windows.ps1 (see bench/routes/section-sync.txt).
Nobody plays on that server while the client is away, so a change "made while the player was
away" is made here instead, the way a server save would make it. Everything after that - the
join span, fetching, merging, saving and both records - runs for real.

Only ever touches .testdata/profiles/<profile> (refuses any other path). Run it with the sandbox
server and client stopped.

  python scripts/section-sync-inject.py status            # both records, no change
  python scripts/section-sync-inject.py rewrite 10        # a server re-save of 10 held level-0 sections
  python scripts/section-sync-inject.py legacy            # an older build's write (no version)

  --profile NAME   the sandbox profile (default: section-sync; Session 109 used phaseb)
"""
import argparse
import glob
import os
import sqlite3
import sys

REPO = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))


def parse():
    parser = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    parser.add_argument('command', nargs='?', default='status', choices=['status', 'rewrite', 'legacy'])
    parser.add_argument('count', nargs='?', type=int, default=10)
    parser.add_argument('--profile', default='section-sync')
    return parser.parse_args()


def one(profile_root, pattern, exclude_server):
    found = [p for p in glob.glob(pattern)
             if p.endswith('.db') and (not exclude_server or not p.endswith('-server.db'))]
    if len(found) != 1:
        sys.exit('expected exactly one cache for %s, found %s' % (pattern, found))
    path = os.path.abspath(found[0])
    if not path.lower().startswith(os.path.abspath(profile_root).lower() + os.sep):
        sys.exit('refusing a path outside the sandbox profile: ' + path)
    wal = path + '-wal'
    if os.path.exists(wal) and os.path.getsize(wal) > 0:
        print('note: %s has a %d-byte WAL (a process may still hold it)'
              % (os.path.basename(path), os.path.getsize(wal)))
    return path


def meta(con, key):
    row = con.execute('SELECT Value FROM Meta WHERE Key=?', (key,)).fetchone()
    return row[0] if row else None


def status(server, client, server_path, client_path):
    print('server cache', os.path.basename(server_path))
    print('  generation', meta(server, 'SectionGeneration'), 'version', meta(server, 'SectionVersion'),
          'legacy mark', meta(server, 'LegacyWrite'))
    print('  rows by level', server.execute('SELECT Detail, COUNT(*) FROM Section GROUP BY Detail').fetchall())
    print('  level-0 versions min/max', server.execute(
        'SELECT MIN(Version), MAX(Version) FROM Section INDEXED BY SectionVersions WHERE Detail=0').fetchone())
    print('client cache', os.path.basename(client_path))
    print('  record: generation', meta(client, 'AssistGeneration'), 'version', meta(client, 'AssistVersion'))
    print('  owed', client.execute('SELECT COUNT(*) FROM AssistOwed').fetchone()[0])
    print('  rows by level', client.execute('SELECT Detail, COUNT(*) FROM Section GROUP BY Detail').fetchall())
    held = set(client.execute('SELECT SX, SZ FROM Section WHERE Detail=0').fetchall())
    both = [r for r in server.execute('SELECT SX, SZ FROM Section WHERE Detail=0').fetchall() if r in held]
    print('  level-0 sections held by both', len(both))
    recorded = meta(client, 'AssistVersion')
    if recorded is not None and meta(client, 'AssistGeneration') == meta(server, 'SectionGeneration'):
        above = server.execute('SELECT SX, SZ FROM Section INDEXED BY SectionVersions '
                               'WHERE Detail=0 AND Version > ?', (int(recorded),)).fetchall()
        print('  the next join should announce %d level-0 sections, %d of them held'
              % (len(above), sum(1 for r in above if r in held)))


def rewrite(server, client, count):
    held = set(client.execute('SELECT SX, SZ FROM Section WHERE Detail=0').fetchall())
    rows = sorted(r for r in server.execute('SELECT SX, SZ FROM Section WHERE Detail=0').fetchall() if r in held)
    if len(rows) < count:
        sys.exit('only %d level-0 sections are held by both caches' % len(rows))
    step = len(rows) // count
    chosen = [rows[i * step] for i in range(count)]
    blobs = [server.execute('SELECT Data FROM Section WHERE Detail=0 AND SX=? AND SZ=?', r).fetchone()[0]
             for r in chosen]
    top = max(int(meta(server, 'SectionVersion') or 0),
              server.execute('SELECT COALESCE(MAX(Version), 0) FROM Section INDEXED BY SectionVersions').fetchone()[0])
    new_version = top + 1
    with server:
        for i, (sx, sz) in enumerate(chosen):
            # A re-save with different content: each row takes the next chosen row's blob, so the
            # client's merge visibly replaces columns. The server's own recapture corrects it later.
            server.execute('UPDATE Section SET Data=?, Version=? WHERE Detail=0 AND SX=? AND SZ=?',
                           (blobs[(i + 1) % count], new_version, sx, sz))
        server.execute("INSERT OR REPLACE INTO Meta (Key, Value) VALUES ('SectionVersion', ?)", (str(new_version),))
    print('rewrote %d held level-0 sections at version %d (was %d)' % (count, new_version, top))


def legacy(server):
    row = server.execute('SELECT SX, SZ, Data, ApplyToParent, ModifiedMs FROM Section WHERE Detail=0 '
                         'ORDER BY SX, SZ LIMIT 1').fetchone()
    with server:
        # Exactly the upsert a build from before section versions issues: it names no Version.
        server.execute('INSERT OR REPLACE INTO Section (Detail, SX, SZ, Data, ApplyToParent, ModifiedMs) '
                       'VALUES (0, ?, ?, ?, ?, ?)', row)
    print('wrote level-0 %d,%d the way an older build would; legacy mark now %s'
          % (row[0], row[1], meta(server, 'LegacyWrite')))


def main():
    args = parse()
    profile_root = os.path.join(REPO, '.testdata', 'profiles', args.profile)
    if not os.path.isdir(profile_root):
        sys.exit('no sandbox profile at ' + profile_root)
    server_path = one(profile_root, os.path.join(profile_root, 'server', 'ModData', 'vintagehorizons', '*-server.db'), False)
    client_path = one(profile_root, os.path.join(profile_root, 'ModData', 'vintagehorizons', '*.db'), True)
    server = sqlite3.connect(server_path)
    client = sqlite3.connect(client_path)
    try:
        if args.command == 'rewrite':
            rewrite(server, client, args.count)
        elif args.command == 'legacy':
            legacy(server)
        status(server, client, server_path, client_path)
    finally:
        server.close()
        client.close()


if __name__ == '__main__':
    main()
