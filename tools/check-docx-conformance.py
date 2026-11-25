#!/usr/bin/env python3
"""Vérifie une règle de la matrice et lance uniquement ses cas ciblés."""
import argparse
import json
from pathlib import Path
import re
import subprocess
import sys

root = Path(__file__).resolve().parents[1]
catalog = json.loads((root / 'docs/architecture-docx/matrice-normative-v1.json').read_text(encoding='utf-8'))
rules = {rule['id']: rule for rule in catalog['regles']}
parser = argparse.ArgumentParser(description=__doc__)
parser.add_argument('--rule', required=True, choices=sorted(rules))
rule = rules[parser.parse_args().rule]
print(json.dumps(rule, ensure_ascii=False, indent=2), flush=True)
required = {'id', 'specifications', 'compatibilite', 'structure', 'impacts',
            'interpretation', 'implementation', 'tests', 'resultat', 'qualification'}
if required != set(rule) or any(key not in catalog['versions'] for key in rule['specifications']):
    sys.exit('Règle incomplète ou édition de référence inconnue.')
for source, members in rule['implementation'].items():
    code = (root / source).read_text(encoding='utf-8')
    for member in members:
        if not re.search(r'\b' + re.escape(member) + r'\s*\(', code):
            sys.exit(f'Méthode introuvable dans {source} — {member}')
core_cases = {'notes-links'}
for case in rule['tests']:
    component = 'core' if case in core_cases else 'viewer'
    result = subprocess.run([sys.executable, str(root / 'tools/check-target.py'),
                             '--component', component, '--case', case], cwd=root)
    if result.returncode:
        sys.exit(result.returncode)
