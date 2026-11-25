#!/usr/bin/env python3
"""Write the French and English seed texts for a Lecteur Word language pack."""

import argparse
from collections import Counter
import hashlib
import json
import re
import unicodedata
from pathlib import Path
from xml.etree import ElementTree


ROOT = Path(__file__).resolve().parents[1] / "src" / "LecteurWord"
LITERAL = r'"(?:[^"\\\r\n]|\\.)*"'
PAIR = rf'({LITERAL})\s*,\s*({LITERAL})'
SIMPLE = re.compile(rf'\bT\(\s*{PAIR}|\bIconButton\(\s*{LITERAL}\s*,\s*{PAIR}')
CHOICES = re.compile(rf'\(\s*(?:ReadingView\.\w+\s*,\s*)?{LITERAL}\s*,\s*(?:{LITERAL}\s*,\s*)?{PAIR}\s*\)')
COUNTS = re.compile(rf'\bLocalCount\(\s*({LITERAL})\s*,[^,]+,\s*{PAIR}\s*,\s*{PAIR}')
FORMATS = re.compile(rf'\bLocalFormat\(\s*({LITERAL})\s*,\s*{PAIR}')
DIAGNOSTICS = re.compile(rf'\["([A-Z][A-Z0-9_]+)"\]\s*=\s*({LITERAL})')
EXTENSION_ERRORS = re.compile(rf'\[({LITERAL})\]\s*=\s*({LITERAL})')
OFFICIAL_CREDITS = re.compile(rf'OfficialCredits\s*=\s*({LITERAL}).*?T\(OfficialCredits,\s*({LITERAL})', re.DOTALL)
SUPPORT_KINDS = re.compile(rf'\(SupportKind\.(\w+),\s*(true|false)\)\s*=>\s*({LITERAL})')
FORMAT_TOKENS = re.compile(r'\{[0-9](?::[NPF][0-3])?\}')
BUILT_IN_LOCALES = frozenset('ar cs da de es fi it ja ko nl pl pt-BR pt-PT ru sv tr uk zh-CN'.split())


def message_key(french, english):
    value = hashlib.sha256(f"{french}\n{english}".encode("utf-8")).hexdigest()
    return f"ui.{value[:24]}"


def decoded(values):
    return tuple(json.loads(value) for value in values)


def source_messages():
    messages = {}
    project = ElementTree.parse(ROOT / "LecteurWord.csproj")
    excluded = {name for item in project.findall(".//Compile[@Remove]") for name in item.attrib["Remove"].split(";")}

    def add_pair(french, english):
        messages[message_key(french, english)] = {"fr": french, "en": english}

    for path in sorted(ROOT.glob("*.cs")):
        if path.name in excluded:
            continue
        source = path.read_text(encoding="utf-8")
        for match in (*SIMPLE.finditer(source), *CHOICES.finditer(source)):
            french, english = decoded(value for value in match.groups() if value is not None)
            add_pair(french, english)
        for match in COUNTS.finditer(source):
            key, fr_one, fr_other, en_one, en_other = decoded(match.groups())
            messages[f"{key}.one"] = {"fr": fr_one, "en": en_one}
            messages[f"{key}.other"] = {"fr": fr_other, "en": en_other}
        for match in FORMATS.finditer(source):
            key, french, english = decoded(match.groups())
            messages[key] = {"fr": french, "en": english}
        if path.name == "ReaderLocalization.cs":
            for match in EXTENSION_ERRORS.finditer(source):
                add_pair(*decoded(match.groups()))
        if path.name == "AboutWindow.cs":
            add_pair(*decoded(OFFICIAL_CREDITS.search(source).groups()))
        if path.name == "ReaderDiagnosticMessages.cs":
            english, french = source.split("FrenchDiagnosticMessages =")
            for locale, section in (("en", english), ("fr", french)):
                for key, value in DIAGNOSTICS.findall(section):
                    messages.setdefault(f"diagnostic.{key.lower()}", {})[locale] = json.loads(value)
    support_labels = {}
    for kind, english, literal in SUPPORT_KINDS.findall((ROOT / "Platform" / "GitHubSupportService.cs").read_text(encoding="utf-8")):
        support_labels.setdefault(kind, {})[english] = json.loads(literal)
    for labels in support_labels.values():
        add_pair(labels["false"], labels.get("true", labels["false"]))
    return messages


def check_built_in(reference):
    paths = sorted((ROOT / "Languages").glob("*.json"))
    if {path.stem for path in paths} != BUILT_IN_LOCALES:
        raise SystemExit("The set of embedded language packs is incomplete or unexpected.")
    count_keys = {key.removesuffix(".other") for key in reference if key.endswith(".other")}
    optional = {f"{key}.{suffix}": reference[f"{key}.other"]
                for key in count_keys for suffix in ("zero", "two", "few", "many")}
    for path in paths:
        pack = json.loads(path.read_text(encoding="utf-8"))
        messages = pack["messages"]
        if pack["culture"] != path.stem or not pack["name"].strip():
            raise SystemExit(f"{path.name} has invalid language metadata.")
        missing = reference.keys() - messages.keys()
        unexpected = messages.keys() - (reference.keys() | optional.keys())
        if missing or unexpected:
            raise SystemExit(f"{path.name} missing {sorted(missing)}, unexpected {sorted(unexpected)}.")
        for key, text in messages.items():
            source = reference.get(key) or optional[key]
            expected = Counter(FORMAT_TOKENS.findall(source.get("en", source.get("fr"))))
            if (not text.strip() or len(text) > 1600 or Counter(FORMAT_TOKENS.findall(text)) != expected
                    or any(unicodedata.category(char) in ("Cc", "Cf") and char not in "\n\t" for char in text)):
                raise SystemExit(f"{path.name} has invalid text or format parameters at {key}.")
            if expected and any(brace in FORMAT_TOKENS.sub("", text) for brace in "{}"):
                raise SystemExit(f"{path.name} has an invalid format at {key}.")
    print(f"{len(paths)} catalogues, {len(reference)} clés de référence par catalogue.")


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--check-built-in", action="store_true", help="Validate installed resources against source messages.")
    options = parser.parse_args()
    reference = source_messages()
    if options.check_built_in:
        check_built_in(reference)
    else:
        print(json.dumps(reference, ensure_ascii=False, sort_keys=True, indent=2))


if __name__ == "__main__":
    main()
