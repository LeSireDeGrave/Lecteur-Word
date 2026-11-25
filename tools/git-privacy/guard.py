#!/usr/bin/env python3
"""Local Git privacy checks for staged and outgoing objects."""
import functools
import hashlib
import json
import os
from pathlib import Path, PurePosixPath
import re
import subprocess
import sys

MAX_BYTES = 16 * 1024 * 1024
HOOKS = {"pre-commit", "pre-merge-commit", "commit-msg", "pre-push"}
OBJECT_ID = re.compile(rb"(?:[0-9a-f]{40}|[0-9a-f]{64})\Z")
PROTECTED_REFS = (b"refs/agents/", b"refs/codex/", b"refs/opus-local-backups/")
CREDENTIAL_FILES = {
    ".git-credentials", ".netrc", ".pypirc", "credentials.json",
    "id_rsa", "id_dsa", "id_ecdsa", "id_ed25519", "secrets.json", "kubeconfig",
    "passwords.csv", "passwords.txt", "mots de passe.csv", "mots de passe.ods",
}
PRIVATE_EXTENSIONS = {
    ".doc", ".docx", ".docm", ".dot", ".dotx", ".dotm", ".odt", ".pdf", ".epub",
    ".nova", ".rtf", ".scrivx", ".pages", ".odg", ".xls", ".xlsx", ".ods", ".ics",
    ".zip", ".7z", ".rar", ".tar", ".gz", ".tgz", ".xz", ".bz2", ".lwext",
    ".png", ".jpg", ".jpeg", ".webp", ".gif", ".bmp", ".tif", ".tiff", ".heic",
    ".ico", ".otf", ".ttf", ".woff", ".woff2", ".avif", ".psd",
    ".db", ".sqlite", ".sqlite3", ".log", ".sarif", ".trx", ".har",
    ".bak", ".backup", ".tmp", ".dump", ".dmp", ".trace", ".kdbx",
}
PRIVATE_COMPONENTS = {
    ".agents", ".aws", ".codex", ".local", ".opusnova",
    "artifacts", "captures", "corpus", "qualification", "qualifications",
    "screenshots", "testresults", "reports", "private",
}
PATTERNS = {
    "clé privée": rb"-----BEGIN [A-Z0-9 ]{0,60}PRIVATE KEY-----|PuTTY-User-Key-File-\d+:",
    "jeton fournisseur": rb"\b(?:gh[pousr]_[A-Za-z0-9]{30,}|github_pat_[A-Za-z0-9_]{30,}|"
                         rb"(?:AKIA|ASIA)[A-Z0-9]{16}|AIza[\w-]{35}|xox[baprs]-[\w-]{12,}|"
                         rb"sk-(?:proj-|ant-|live-|test-)?[A-Za-z0-9_-]{20,}|"
                         rb"(?:sk|rk)_live_[A-Za-z0-9]{16,}|hf_[A-Za-z0-9]{24,}|"
                         rb"glpat-[A-Za-z0-9_-]{20,}|npm_[A-Za-z0-9]{30,}|"
                         rb"pypi-[A-Za-z0-9_-]{24,})\b",
    "jeton JWT": rb"\beyJ[A-Za-z0-9_-]{8,}\.eyJ[A-Za-z0-9_-]{8,}\.[A-Za-z0-9_-]{12,}\b",
    "identifiants dans une URL": rb"[a-zA-Z][a-zA-Z0-9+.-]*://[^\s/:@]{1,100}:[^\s/@]{4,150}@",
    "en-tête d’autorisation": rb"(?i)\b(?:Bearer|Basic)\s+([a-z0-9_+/=.-]{20,})",
    "valeur de sécurité en clair":
        rb"""(?im)(?:^|[\s{,(])(?:export\s+)?["']?(?:[\w-]+[_-])?"""
        rb"""(?:api[_-]?key|(?:access|auth|refresh|personal[_-]access)[_-]?token|"""
        rb"""client[_-]?secret|password|passwd|secret[_-]?key)["']?\s*[:=]\s*"""
        rb"""(?:"([^"\r\n]{8,})"|'([^'\r\n]{8,})'|([A-Za-z0-9_./+@=-]{12,})(?=\s|$))""",
    "chemin utilisateur réel": rb"(?i)(?:/home/(?!user/|username/|example/|\$)[\w.-]+/"
                               rb"|[A-Z]:[\\/]Users[\\/](?!Public[\\/]|User[\\/]|Example[\\/])[\w.-]+[\\/])",
}
PATTERNS = {name: re.compile(pattern) for name, pattern in PATTERNS.items()}
EMAIL = re.compile(rb"(?i)\b[a-z0-9.!#$%&'*+/=?^_`{|}~-]+@[a-z0-9-]+(?:\.[a-z0-9-]+)+")
PLACEHOLDER = re.compile(
    rb"(?i)^(?:\$\{[^{}]+\}|\$[a-z_]\w*|<[^<>]+>|"
    rb"(?:example|sample|dummy|fixture|changeme|replace[_-]?me|your)[_-][a-z0-9_-]+)$"
)


class CheckError(Exception):
    pass


def digest(content):
    return hashlib.sha256(content).hexdigest()


def git(*args):
    environment = {**os.environ, "GIT_NO_REPLACE_OBJECTS": "1", "GIT_OPTIONAL_LOCKS": "0"}
    run = subprocess.run(
        ("git", "--no-pager", *args), stdout=subprocess.PIPE, stderr=subprocess.DEVNULL,
        env=environment, check=False,
    )
    if run.returncode:
        raise CheckError(f"Git n’a pas pu lire les données ({args[0]}).")
    return run.stdout


def object_id(value):
    if not OBJECT_ID.fullmatch(value):
        raise CheckError("Identifiant d’objet Git invalide.")
    return value.decode("ascii")


def public_email(value):
    domain = value.lower().rsplit(b"@", 1)[-1]
    return domain in {b"users.noreply.github.com", b"example.com", b"example.net", b"example.org"} or (
        domain.endswith((b".test", b".invalid"))
    )


class Guard:
    def __init__(self, policy):
        self.policy = policy
        self.seen_blobs = set()
        self.findings = set()

    def reject(self, rule, path, commit):
        raw = path.encode("utf-8", "surrogateescape")
        if not re.fullmatch(r"[A-Za-z0-9_./<> -]+", path) or any(self.matches(raw)):
            path = f"lieu #{digest(raw)[:12]}"
        location = f"{commit[:12]} {path}" if commit else path
        finding = (location, rule)
        if finding not in self.findings:
            print(f"SECURITY-GIT-001 · {location} · {rule}", file=sys.stderr)
            self.findings.add(finding)

    def matches(self, data):
        for rule, pattern in PATTERNS.items():
            for match in pattern.finditer(data):
                value = next((part for part in match.groups() if part), match.group())
                if rule != "valeur de sécurité en clair" or not PLACEHOLDER.fullmatch(value):
                    yield rule, value
        for match in EMAIL.finditer(data):
            if not public_email(match.group()):
                yield "courriel non validé", match.group()

    def inspect_text(self, data, path, commit=None):
        allowed = self.policy["matches"].get(path, [])
        for rule, value in self.matches(data):
            if digest(value) not in allowed:
                self.reject(rule, path, commit)

    def inspect_identity(self, raw, path, commit=None):
        email = re.search(rb"<([^<>]+)> \d+ [+-]\d{4}$", raw)
        if email is None:
            self.reject("identité Git illisible", path, commit)
            return
        address = email[1]
        if not EMAIL.fullmatch(address) or not public_email(address):
            self.reject("adresse Git sans protection", path, commit)
        for rule, _ in self.matches(raw[:email.start()]):
            self.reject(rule, path, commit)

    def path_rule(self, path):
        p = PurePosixPath(path.casefold())
        if p.name in {"agents.md", "agents.private.md"}:
            return "consignes locales"
        if (p.name in CREDENTIAL_FILES or p.suffix in {".key", ".pem", ".p12", ".pfx", ".snk"}
                or p.name == ".env" or p.name.startswith(".env.") and p.name not in {
                    ".env.example", ".env.sample",
                }):
            return "fichier d’identifiants"
        if any(prefix.casefold() in (str(p), *map(str, p.parents))
               for prefix in self.policy["private_paths"]):
            return "données locales ou de qualification"
        if PRIVATE_COMPONENTS.intersection(p.parts[:-1]):
            return "données locales ou de qualification"
        if p.suffix in PRIVATE_EXTENSIONS:
            return "document, image ou export non approuvé"
        return None

    def inspect_blob(self, oid, mode, path, commit=None):
        key = (oid, mode, path)
        if key in self.seen_blobs:
            return
        self.seen_blobs.add(key)
        self.inspect_text(path.encode("utf-8", "surrogateescape"), "<nom de fichier>", commit)
        if mode not in {b"100644", b"100755", b"120000"}:
            self.reject("type Git non contrôlable", path, commit)
            return
        expected = self.policy["files"].get(path)
        if not expected and (rule := self.path_rule(path)):
            self.reject(rule, path, commit)
            return
        if int(git("cat-file", "-s", oid)) > MAX_BYTES:
            self.reject("contenu trop volumineux pour le contrôle local", path, commit)
            return
        data = git("cat-file", "blob", oid)
        if expected:
            if mode == b"120000" or digest(data) != expected:
                self.reject("contenu différent de la ressource approuvée", path, commit)
            return
        if mode == b"120000":
            self.reject("lien symbolique non approuvé", path, commit)
            return
        try:
            if b"\0" in data:
                raise UnicodeError
            data.decode("utf-8")
        except UnicodeError:
            self.reject("contenu binaire ou encodage non approuvé", path, commit)
            return
        self.inspect_text(data, path, commit)

    def inspect_diff(self, raw, commit=None):
        records = iter(raw.split(b"\0")[:-1])
        for header in records:
            previous, mode, _, oid, status = header.split()
            path = next(records).decode("utf-8", "surrogateescape")
            if previous[:1] != b":" or status[:1] not in b"ACMRT":
                raise CheckError("Format de différence Git inattendu.")
            self.inspect_blob(object_id(oid), mode, path, commit)

    def inspect_identity_and_diff(self):
        self.inspect_identity(git("var", "GIT_AUTHOR_IDENT").rstrip(), "<auteur>")
        self.inspect_identity(git("var", "GIT_COMMITTER_IDENT").rstrip(), "<committer>")
        self.inspect_diff(git("diff", "--cached", *DIFF))

    def inspect_message(self, data, path, commit=None):
        if len(data) > MAX_BYTES:
            self.reject("message trop volumineux", path, commit)
        else:
            self.inspect_text(data, path, commit)

    def peel(self, oid, inspect):
        while git("cat-file", "-t", oid) == b"tag\n":
            data = git("cat-file", "tag", oid)
            headers, _, message = data.partition(b"\n\n")
            fields = dict(line.split(b" ", 1) for line in headers.splitlines() if b" " in line)
            if inspect:
                self.inspect_identity(fields.get(b"tagger", b""), "<auteur du tag>", oid)
                self.inspect_message(fields[b"tag"] + b"\n" + message, "<message du tag>", oid)
            oid = object_id(fields[b"object"])
        if git("cat-file", "-t", oid) != b"commit\n":
            raise CheckError("Un objet sortant ne désigne pas un commit.")
        return oid

    def inspect_commit(self, oid):
        data = git("cat-file", "commit", oid)
        headers, _, message = data.partition(b"\n\n")
        parent = None
        for line in headers.split(b"\n"):
            field, _, value = line.partition(b" ")
            if field == b"parent" and parent is None:
                parent = object_id(value)
            elif field in {b"author", b"committer"}:
                self.inspect_identity(value, "<identité du commit>", oid)
        self.inspect_message(message, "<message du commit>", oid)
        comparison = (parent, oid) if parent else ("--root", oid)
        self.inspect_diff(git("diff-tree", "--no-commit-id", "-r", *DIFF, *comparison), oid)

    def pre_push(self, updates):
        if git("rev-parse", "--is-shallow-repository") != b"false\n":
            raise CheckError("Historique incomplet disponible pour la vérification.")
        grafts = Path(os.fsdecode(git("rev-parse", "--git-path", "info/grafts").rstrip(b"\n")))
        if grafts.is_file() and grafts.stat().st_size:
            raise CheckError("L’historique local utilise un fichier de greffes.")
        targets, bases = set(), set()
        for update in updates:
            source, local, destination, previous = update.split()
            local, previous = object_id(local), object_id(previous)
            if source.startswith(PROTECTED_REFS) or not destination.startswith((b"refs/heads/", b"refs/tags/")):
                self.reject("référence de publication hors périmètre", "<référence Git>", None)
            self.inspect_text(source + b" " + destination, "<nom de référence>")
            if set(local) == {"0"}:
                continue
            targets.add(self.peel(local, inspect=True))
            if set(previous) != {"0"}:
                bases.add(self.peel(previous, inspect=False))
        if targets:
            revisions = "".join(oid + "\n" for oid in sorted(targets))
            revisions += "".join("^" + oid + "\n" for oid in sorted(bases))
            commits = subprocess.run(
                ["git", "--no-pager", "rev-list", "--stdin"], input=revisions.encode("ascii"),
                stdout=subprocess.PIPE, stderr=subprocess.DEVNULL, check=False,
                env={**os.environ, "GIT_NO_REPLACE_OBJECTS": "1", "GIT_OPTIONAL_LOCKS": "0"},
            )
            if commits.returncode:
                raise CheckError("L’historique sortant ne peut pas être vérifié.")
            for oid in commits.stdout.splitlines():
                self.inspect_commit(object_id(oid))


DIFF = ("--raw", "--no-abbrev", "--no-renames", "--diff-filter=ACMRT", "-z")


def main():
    policy = json.loads(Path(__file__).with_name("policy.json").read_text(encoding="utf-8"))
    guard = Guard(policy)
    hook = sys.argv[1]
    if hook in {"pre-commit", "pre-merge-commit"}:
        guard.inspect_identity_and_diff()
    elif hook == "commit-msg":
        guard.inspect_message(Path(sys.argv[2]).read_bytes(), "<message du commit>")
    elif hook == "pre-push":
        guard.pre_push(sys.stdin.buffer)
    else:
        raise CheckError("Point d’entrée Git inattendu.")
    return int(bool(guard.findings))


if __name__ == "__main__":
    try:
        sys.exit(main())
    except CheckError as error:
        print(f"SECURITY-GIT-001 · {error}", file=sys.stderr)
        sys.exit(1)
    except (OSError, KeyError, ValueError, IndexError, StopIteration):
        print("SECURITY-GIT-001 · Le contrôle de confidentialité a échoué.", file=sys.stderr)
        sys.exit(1)
