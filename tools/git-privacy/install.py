#!/usr/bin/env python3
"""Install the reviewed hooks in the local Git metadata."""
import hashlib
import os
from pathlib import Path
import subprocess
import sys
import tempfile

HOOKS = ("pre-commit", "pre-merge-commit", "commit-msg", "pre-push")
MARKER = "# SECURITY-GIT-001 — contrôles locaux"
SOURCE = Path(__file__).resolve().parent


def git(*args):
    return subprocess.run(("git", *args), capture_output=True, check=False, cwd=SOURCE)


def location():
    configured = git("config", "--get-all", "core.hooksPath")
    if configured.returncode != 1:
        raise RuntimeError("core.hooksPath possède déjà une configuration. Installation interrompue.")
    result = git("rev-parse", "--path-format=absolute", "--git-path", "hooks")
    if result.returncode:
        raise RuntimeError("Impossible de déterminer le répertoire des hooks.")
    return Path(os.fsdecode(result.stdout.rstrip(b"\n")))


def replace(path, data, mode):
    with tempfile.NamedTemporaryFile(dir=path.parent, delete=False) as stream:
        temporary = Path(stream.name)
        stream.write(data)
    try:
        temporary.chmod(mode)
        temporary.replace(path)
    finally:
        temporary.unlink(missing_ok=True)


def main():
    hooks = location()
    for name in HOOKS:
        path = hooks / name
        if path.is_symlink() or path.exists() and (
            not path.is_file() or MARKER not in path.read_text(encoding="utf-8", errors="replace")
        ):
            raise RuntimeError(f"Hook existant à préserver ({name}).")
    source_files = {name: (SOURCE / name).read_bytes() for name in ("guard.py", "policy.json")}
    version = hashlib.sha256(b"\0".join(source_files.values())).hexdigest()
    package = f"security-git-001-{version[:16]}"
    hooks.mkdir(parents=True, exist_ok=True)
    installed = hooks / package
    if installed.is_symlink():
        raise RuntimeError("Le répertoire d’installation est un lien symbolique.")
    installed.mkdir(exist_ok=True)
    for name, content in source_files.items():
        replace(installed / name, content, 0o644)
    for name in HOOKS:
        launcher = f'''#!/bin/sh
{MARKER}
exec python3 -I "$(dirname -- "$0")/{package}/guard.py" "{name}" "$@"
'''
        replace(hooks / name, launcher.encode(), 0o755)
    print("SECURITY-GIT-001 · Hooks locaux installés.")


if __name__ == "__main__":
    try:
        main()
    except (OSError, RuntimeError) as error:
        sys.exit(f"SECURITY-GIT-001 · {error}")
