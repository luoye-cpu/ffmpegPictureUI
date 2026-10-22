"""Sweep stale artifacts out of tests/output — the single implementation behind cleanup-structure.ps1.

Why this exists: two gates only delete their work dir on a green run (`if ($fail -eq 0)`), so every red
round leaves a full tree behind; 29 GB / 1654 entries accumulated that way. Red-round evidence is worth
keeping, so the rule is *age + reference*, not *colour*:

  protected  : the 3 git worktrees mounted under tests/output, anything still referenced by a tracked
               script/source/doc, and entries newer than --keep-after
  deletable  : unreferenced entries older than --keep-after, plus empty directories anywhere below

Default is dry-run; pass --apply to delete. Never follows reparse points.
"""
import argparse
import os
import re
import sys

REPO = os.path.abspath(os.path.join(os.path.dirname(__file__), '..', '..'))
OUT = os.path.join(REPO, 'tests', 'output')

# worktrees live under tests/output (git worktree list) — deleting them would destroy other branches' trees
WORKTREES = ['_wt_gm3', '_wt_verify', 'review']
TEXT_EXT = ('.ps1', '.py', '.cs', '.csproj', '.md', '.sln', '.json', '.xaml', '.axaml', '.props', '.psm1')
SKIP_DIRS = {'bin', 'obj', '.git', '__pycache__', 'output', 'publish', 'node_modules'}


def tracked_text_corpus():
    """Every tracked text file's contents, so a name that any gate still points at is never deleted."""
    import subprocess
    files = subprocess.run(['git', '-c', 'core.quotepath=false', 'ls-files'],
                           capture_output=True, text=True, cwd=REPO).stdout.splitlines()
    blob = []
    for f in files:
        if not f.endswith(TEXT_EXT):
            continue
        p = os.path.join(REPO, f.replace('/', os.sep))
        try:
            with open(p, encoding='utf-8', errors='replace') as h:
                blob.append(h.read())
        except OSError:
            pass
    return '\n'.join(blob)


def dir_size(path):
    if os.path.isfile(path):
        try:
            return os.path.getsize(path)
        except OSError:
            return 0
    total = 0
    for r, d, fs in os.walk(path):
        d[:] = [x for x in d if not is_reparse(os.path.join(r, x))]
        for f in fs:
            try:
                total += os.path.getsize(os.path.join(r, f))
            except OSError:
                pass
    return total


def is_reparse(path):
    try:
        return bool(os.stat(path, follow_symlinks=False).st_file_attributes & 0x400)
    except (OSError, AttributeError):
        return False


def empty_dirs(root):
    out = []
    for r, d, fs in os.walk(root, topdown=False):
        if is_reparse(r):
            continue
        if not fs and not [x for x in d if not is_reparse(os.path.join(r, x))]:
            out.append(r)
    return out


def run_dirs_corpus():
    """Top-level dirs under tests/output that can hold per-round workspaces."""
    out = []
    for n in sorted(os.listdir(OUT)):
        p = os.path.join(OUT, n)
        if n in WORKTREES or n in ('_layout',) or not os.path.isdir(p) or is_reparse(p):
            continue
        out.append(p)
    return out


def iter_run_dirs(corpus):
    """Yield candidate per-round dirs: children of a parent that look like one round's workspace."""
    protected_names = ('sources', 'raw_samples', 'raw_samples3', 'fixtures', 'bin', 'obj')
    for parent in run_dirs_corpus():
        pname = os.path.relpath(parent, OUT)
        if pname in protected_names:
            continue
        try:
            kids = sorted(os.listdir(parent))
        except OSError:
            continue
        for k in kids:
            p = os.path.join(parent, k)
            if not os.path.isdir(p) or is_reparse(p):
                continue
            if k in protected_names:
                continue
            rel = os.path.relpath(p, OUT).replace(os.sep, '/')
            if rel in corpus or ('tests/output/' + rel) in corpus:
                yield p, 'referenced'
                continue
            yield p, 'candidate'


def sweep_runs(apply, max_age_days, corpus):
    """Delete per-round workspaces older than max_age_days (GUID dirs are per-round by design).

    Red-round evidence stays for max_age_days; after that a stale round has no reader, because the next
    round mints a fresh GUID dir and never looks at the old one.
    """
    import time
    cutoff = time.time() - max_age_days * 86400
    stale, kept, refed = [], 0, 0
    for p, kind in iter_run_dirs(corpus):
        if kind == 'referenced':
            refed += 1
            continue
        if os.path.getmtime(p) < cutoff:
            stale.append(p)
        else:
            kept += 1
    freed = sum(dir_size(p) for p in stale)
    print('run-dirs: 陈旧可删 %d 个 = %.2f GB ｜ 保留期内 %d ｜ 被在册文本点名 %d'
          % (len(stale), freed / 2**30, kept, refed))
    for p in sorted(stale, key=dir_size, reverse=True)[:12]:
        print('    %8.1f MB  %s' % (dir_size(p) / 2**20, os.path.relpath(p, OUT)))
    if not apply:
        return 0
    import shutil
    import time
    mpath = os.path.join(OUT, '_layout', 'sweep-runs-%s.tsv' % time.strftime('%Y%m%d-%H%M%S'))
    os.makedirs(os.path.dirname(mpath), exist_ok=True)
    with open(mpath, 'w', encoding='utf-8') as h:
        h.write('# per-round dirs older than %d days, no tracked-text reference\n' % max_age_days)
        for p in stale:
            h.write('%s\t%d\n' % (os.path.relpath(p, OUT), dir_size(p)))
    print('manifest: %s (%d rows)' % (os.path.relpath(mpath, REPO), len(stale)))
    n = 0
    for p in stale:
        try:
            shutil.rmtree(p)
            n += 1
        except OSError:
            pass
    return n


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument('--keep-after', default='2026-10-01', help='entries newer than this date are kept')
    ap.add_argument('--runs', action='store_true', help='sweep per-round workspaces under tests/output by age')
    ap.add_argument('--unreferenced', action='store_true',
                    help='delete top-level entries no tracked text mentions at all, ignoring --keep-after')
    ap.add_argument('--max-age-days', type=int, default=7, help='age limit for --runs')
    ap.add_argument('--apply', action='store_true')
    args = ap.parse_args()

    corpus = tracked_text_corpus()
    if args.runs:
        removed = sweep_runs(args.apply, args.max_age_days, corpus)
        print('applied run-dir sweep: %d removed' % removed) if args.apply else print('\nDRY RUN — pass --apply')
        return

    cutoff = None
    for fmt in ('%Y-%m-%d',):
        try:
            cutoff = __import__('time').mktime(__import__('time').strptime(args.keep_after, fmt))
        except ValueError:
            pass
    print('reference corpus: %d chars from tracked text files' % len(corpus))

    entries = sorted(os.listdir(OUT))
    dele, refed, fresh, protected = [], [], [], []
    for name in entries:
        p = os.path.join(OUT, name)
        if name in WORKTREES:
            protected.append(name)
            continue
        if re.search(r'(?<![0-9A-Za-z_.-])' + re.escape(name) + r'(?![0-9A-Za-z_-])', corpus):
            refed.append(name)
            continue
        if not args.unreferenced and os.path.getmtime(p) > cutoff:
            fresh.append(name)
            continue
        dele.append(name)

    bytes_freed = sum(dir_size(os.path.join(OUT, n)) for n in dele)
    empties = empty_dirs(OUT)
    print('top-level entries: %d' % len(entries))
    print('  protected (worktree)        : %d' % len(protected))
    print('  referenced by tracked text  : %d' % len(refed))
    print('  newer than %s       : %d' % (args.keep_after, len(fresh)))
    print('  DELETABLE (stale+unreferenced): %d  = %.1f GB' % (len(dele), bytes_freed / 2**30))
    print('  empty dirs anywhere below   : %d' % len(empties))
    for n in dele[:25]:
        print('    del  %-46s %6.1f MB' % (n, dir_size(os.path.join(OUT, n)) / 2**20))

    if not args.apply:
        print('\nDRY RUN — nothing deleted. Pass --apply.')
        return
    manifest_dir = os.path.join(OUT, '_layout')
    os.makedirs(manifest_dir, exist_ok=True)
    stamp = __import__('time').strftime('%Y%m%d-%H%M%S')
    with open(os.path.join(manifest_dir, 'sweep-%s.tsv' % stamp), 'w', encoding='utf-8') as h:
        h.write('# deletable entries (unreferenced by tracked text AND older than %s)\n' % args.keep_after)
        for n in dele:
            h.write('%s\t%d\n' % (n, dir_size(os.path.join(OUT, n))))
    print('manifest written: tests/output/_layout/sweep-%s.tsv (%d rows)' % (stamp, len(dele)))
    import shutil
    removed = 0
    for n in dele:
        p = os.path.join(OUT, n)
        try:
            if is_reparse(p):
                # a junction must be unlinked, never traversed: rmtree would delete the target tree
                os.rmdir(p)
            elif os.path.isdir(p):
                shutil.rmtree(p)
            else:
                os.remove(p)
            removed += 1
        except OSError as e:
            print('  SKIP %s (%s)' % (n, e.strerror))
    removed_empty = 0
    for d in empty_dirs(OUT):
        try:
            os.rmdir(d)
            removed_empty += 1
        except OSError:
            pass
    print('applied: %d/%d entries removed (%.1f GB), %d empty dirs removed'
          % (removed, len(dele), bytes_freed / 2**30, removed_empty))


if __name__ == '__main__':
    sys.exit(main())
