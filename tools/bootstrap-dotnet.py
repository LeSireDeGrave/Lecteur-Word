#!/usr/bin/env python3
"""Fetch only the official pinned SDK into this workspace; verify SHA-512."""
import hashlib, json, pathlib, tarfile, urllib.request
root = pathlib.Path(__file__).resolve().parents[1]
dest = root / '.local' / 'dotnet'
metadata = json.load(urllib.request.urlopen('https://dotnetcli.blob.core.windows.net/dotnet/release-metadata/10.0/releases.json', timeout=30))
release = next(r for r in metadata['releases'] if r['release-version'] == '10.0.15')
sdk = next(s for s in release['sdks'] if s['version'] == '10.0.404')
file = next(f for f in sdk['files'] if f['rid'] == 'linux-x64' and f['name'].endswith('.tar.gz'))
archive = root / '.local' / 'sdk.tar.gz'
with urllib.request.urlopen(file['url'], timeout=60) as response, archive.open('wb') as out:
    while data := response.read(1024*1024): out.write(data)
assert hashlib.file_digest(archive.open('rb'), 'sha512').hexdigest() == file['hash'].lower(), 'SDK integrity mismatch'
dest.mkdir(exist_ok=True)
with tarfile.open(archive) as tar: tar.extractall(dest, filter='data')
archive.unlink()
print('SDK verified:', sdk['version'], 'runtime', release['release-version'])
