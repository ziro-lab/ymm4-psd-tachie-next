#!/usr/bin/env python3
"""Fetch public MIT parser source; apply one audited fix only to a separate build copy."""
from pathlib import Path
import hashlib
import json
import subprocess
import sys

ROOT = Path(__file__).resolve().parents[1]
DEST = ROOT / '.deps' / 'PsdParser'
URL = 'https://github.com/manju-summoner/PsdParser.git'
REV = 'ff3aee18a95e5fb6e868585a5b0ad15f46decd89'
PATCH_ID = 'psb-section-length-v1'

def git(*args: str, capture: bool = False) -> str:
    proc = subprocess.run(['git', '-C', str(DEST), *args], check=True,
                          text=True, stdout=subprocess.PIPE if capture else None, timeout=120)
    return proc.stdout.strip() if capture else ''

def main() -> None:
    DEST.mkdir(parents=True, exist_ok=True)
    if not (DEST / '.git').exists():
        if any(DEST.iterdir()):
            raise RuntimeError('Dependency destination is not empty; refusing to overwrite it.')
        git('init', '--quiet')
        git('remote', 'add', 'origin', URL)
    if git('remote', 'get-url', 'origin', capture=True) != URL:
        raise RuntimeError('Dependency origin mismatch.')
    has_head = subprocess.run(['git', '-C', str(DEST), 'rev-parse', '--verify', 'HEAD'],
                              text=True, capture_output=True, timeout=10)
    if has_head.returncode == 0:
        if has_head.stdout.strip() != REV or git('status', '--porcelain', capture=True):
            raise RuntimeError('Dependency revision/worktree mismatch; refusing to overwrite local changes.')
    else:
        git('fetch', '--depth=1', '--no-tags', 'origin', REV)
        git('checkout', '--detach', '--quiet', 'FETCH_HEAD')
    if git('rev-parse', 'HEAD', capture=True) != REV:
        raise RuntimeError('Pinned parser commit verification failed.')

    original = DEST / 'PsdParser' / 'LayerAndMaskInformationSection.cs'
    text = original.read_text(encoding='utf-8-sig')
    before = 'reader.BaseStream.Position == position + 4 + Length'
    after = 'reader.BaseStream.Position == position + lengthSize + Length'
    if text.count(before) != 1:
        raise RuntimeError('Parser patch context mismatch. Review the dependency rather than guessing.')
    patched = text.replace(before, after)
    patch_dir = ROOT / '.deps' / 'patched-src'
    patch_dir.mkdir(parents=True, exist_ok=True)
    patched_file = patch_dir / original.name
    patched_file.write_text(patched, encoding='utf-8', newline='\n')
    provenance = {'upstream_commit': REV, 'patch_id': PATCH_ID, 'assembly_name': 'PsdTachieNext.Parser',
                  'original_sha256': hashlib.sha256(original.read_bytes()).hexdigest(),
                  'patched_sha256': hashlib.sha256(patched_file.read_bytes()).hexdigest()}
    (patch_dir / 'provenance.json').write_text(json.dumps(provenance, indent=2) + '\n')
    print(f'PsdParser source pinned: {REV}; local build patch: {PATCH_ID}')

if __name__ == '__main__':
    try:
        main()
    except (RuntimeError, subprocess.SubprocessError) as exc:
        print(f'prepare_parser failed: {exc}', file=sys.stderr)
        sys.exit(1)
