#!/usr/bin/env python3
"""Exercise the actual Git hook entry points without making commits or pushing."""
import hashlib
import json
import os
from pathlib import Path
import shutil
import subprocess
import sys
import tempfile

SOURCE = Path(__file__).resolve().parent
FICTITIOUS = b"gh" + b"p_" + b"X0" * 18
IDENTITY = b"Fixture <robot@example.test> 1700000000 +0000"
ZERO = b"0" * 40
ENVIRONMENT = {key: value for key, value in os.environ.items() if not key.startswith("GIT_")}
ENVIRONMENT.update(GIT_CONFIG_NOSYSTEM="1", GIT_CONFIG_GLOBAL="/dev/null")


class Fixture:
    def __init__(self, root):
        self.root = root
        self.run("init", "-q", "--object-format=sha1")
        self.run("config", "user.email", "robot@example.test")
        self.run("config", "user.name", "Fixture")
        scripts = root / "guard-source"
        scripts.mkdir()
        for name in ("guard.py", "install.py"):
            shutil.copyfile(SOURCE / name, scripts / name)
        self.sample = b"approved synthetic example\0"
        name = "examples/approved.nova"
        policy = {
            "files": {name: hashlib.sha256(self.sample).hexdigest()},
            "matches": {},
            "private_paths": ["exemples"],
        }
        (scripts / "policy.json").write_text(json.dumps(policy))
        subprocess.run([sys.executable, scripts / "install.py"], cwd=root, env=ENVIRONMENT, check=True)
        self.write(".gitignore", b"*.nova\n")
        self.write("src/main.rs", b'fn main() {}\n')
        self.write(name, self.sample)
        self.run("add", ".gitignore", "src/main.rs")
        self.run("add", "-f", name)

    def run(self, *args, data=None, result=0, contains=None):
        process = subprocess.run(
            ["git", "-C", self.root, *args], input=data, capture_output=True, env=ENVIRONMENT,
        )
        assert FICTITIOUS not in process.stderr
        assert process.returncode == result, (args[0], result, process.returncode)
        if contains:
            assert contains.encode() in process.stderr
        return process.stdout.strip()

    def write(self, path, data):
        path = self.root / path
        path.parent.mkdir(parents=True, exist_ok=True)
        path.write_bytes(data)

    def object(self, message=b"Synthetic change", parents=(), identity=IDENTITY):
        tree = self.run("write-tree")
        headers = [b"tree " + tree, *(b"parent " + parent for parent in parents),
                   b"author " + identity, b"committer " + identity]
        return self.run("hash-object", "-t", "commit", "-w", "--stdin",
                        data=b"\n".join(headers) + b"\n\n" + message + b"\n")

    def outgoing(self, oid, previous, result=0, contains=None,
                 source=b"refs/heads/main", destination=b"refs/heads/main"):
        self.write("updates", b" ".join((source, oid, destination, previous)) + b"\n")
        self.run("hook", "run", "--to-stdin=updates", "pre-push", "--", "origin",
                 "https://example.test/repo.git", result=result, contains=contains)


def main():
    with tempfile.TemporaryDirectory(prefix="security-git-fixture-") as tmp:
        f = Fixture(Path(tmp))
        baseline = f.object()
        f.run("hook", "run", "pre-commit")
        f.run("commit", "--dry-run", "-m", "Validation fictive")
        f.write("message", b"Changement synthetique\n")
        f.run("hook", "run", "commit-msg", "--", "message")
        f.outgoing(baseline, ZERO)

        # Unstaged text is irrelevant. The exact staged blob must be checked.
        f.write("src/main.rs", b'const ALT: &str = "' + FICTITIOUS + b'";\n')
        f.run("hook", "run", "pre-commit")
        f.run("add", "src/main.rs")
        f.write("src/main.rs", b"fn main() {}\n")
        f.run("hook", "run", "pre-commit", result=1, contains="jeton fournisseur")
        f.run("hook", "run", "pre-merge-commit", result=1, contains="jeton fournisseur")
        leak = f.object(parents=(baseline,))
        f.run("add", "src/main.rs")
        cleaned = f.object(parents=(leak,))
        f.outgoing(cleaned, baseline, result=1, contains="jeton fournisseur")
        f.outgoing(cleaned, leak)
        f.outgoing(f.object(parents=(baseline, cleaned)), baseline, result=1, contains="jeton fournisseur")

        # A credential in a message or annotated tag also stays in Git history.
        f.write("message", FICTITIOUS + b"\n")
        f.run("hook", "run", "commit-msg", "--", "message", result=1, contains="jeton fournisseur")
        f.outgoing(f.object(FICTITIOUS, (baseline,)), baseline, result=1, contains="jeton fournisseur")
        tag = f.run("hash-object", "-t", "tag", "-w", "--stdin",
                    data=b"object " + baseline + b"\ntype commit\ntag demo\ntagger " +
                    IDENTITY + b"\n\n" + FICTITIOUS + b"\n")
        f.outgoing(tag, ZERO, result=1, contains="jeton fournisseur",
                   source=b"refs/tags/demo", destination=b"refs/tags/demo")
        f.outgoing(baseline, ZERO, result=1, contains="hors périmètre",
                   source=b"refs/codex/interne", destination=b"refs/heads/demo")

        f.write("exemples/prive.nova", b"fictitious private fixture")
        f.run("add", "-f", "exemples/prive.nova")
        f.run("hook", "run", "pre-commit", result=1, contains="données locales")
        hidden = f.object(parents=(baseline,))
        f.run("rm", "--cached", "exemples/prive.nova")
        f.outgoing(f.object(parents=(hidden,)), baseline, result=1, contains="données locales")

        f.write("examples/approved.nova", b"substituted data")
        f.run("add", "-f", "examples/approved.nova")
        f.run("hook", "run", "pre-commit", result=1, contains="ressource approuvée")
        f.outgoing(f.object(parents=(baseline,)), baseline, result=1, contains="ressource approuvée")
        f.write("examples/approved.nova", f.sample)
        f.run("add", "-f", "examples/approved.nova")
        f.run("hook", "run", "pre-commit")

        private_ident = b"Fixture <robot@" + b"security.example> 1700000000 +0000"
        f.outgoing(f.object(parents=(baseline,), identity=private_ident), baseline,
                   result=1, contains="adresse Git sans protection")
        f.outgoing(baseline, b"f" * 40, result=1, contains="n’a pas pu lire")
        f.run("diff", "--cached", "--check")
        assert not f.run("for-each-ref")
    print("OK — index, adoption de ressources, secrets, commits supprimés de l’arbre, fusion, tags, identité et échec sûr.")


if __name__ == "__main__":
    main()
