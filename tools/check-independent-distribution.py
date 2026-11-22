#!/usr/bin/env python3
"""Audit one fresh Linux reader publication; never install or change its contents."""
import argparse
import hashlib
import json
from pathlib import Path


def inspect(directory):
    root = Path(directory).resolve(strict=True)
    for name in ['LecteurWord', 'LecteurWord.dll', 'LecteurWord.deps.json',
                 'LecteurWord.runtimeconfig.json', 'DocxCore.dll', 'DocxFlow.dll']:
        if not (root / name).is_file():
            raise ValueError('Publication incomplète : ' + name)
    deps = json.loads((root / 'LecteurWord.deps.json').read_text())
    projects = {key.split('/')[0] for key, value in deps['libraries'].items()
                if value['type'] == 'project'}
    if projects != {'LecteurWord', 'DocxCore', 'DocxFlow'}:
        raise ValueError('Graphe de projets inattendu : ' + ', '.join(sorted(projects)))
    retired = ['docxengine.experimental', 'elixiroffice', 'onlyoffice', 'euro-office']
    if any(token in key.lower() for key in deps['libraries'] for token in retired):
        raise ValueError('Dépendance à un moteur écarté')
    forbidden_names = {'engine.json', 'sdk-all.js', 'sdk-all-min.js', 'allfonts.js',
                       'brave', 'chrome', 'chromium', 'chrome_crashpad_handler', 'x2t.wasm'}
    files = []
    for path in sorted(root.rglob('*')):
        if path.is_symlink():
            raise ValueError('Lien symbolique à examiner : ' + str(path.relative_to(root)))
        if not path.is_file():
            continue
        relative = str(path.relative_to(root))
        if path.name.lower() in forbidden_names or any(t in relative.lower() for t in retired):
            raise ValueError('Fichier du moteur écarté : ' + relative)
        digest = hashlib.sha256()
        with path.open('rb') as source:
            while block := source.read(1024 * 1024):
                digest.update(block)
        files.append({'path': relative, 'bytes': path.stat().st_size, 'sha256': digest.hexdigest()})
    for name in ['LecteurWord.dll', 'DocxCore.dll', 'DocxFlow.dll']:
        data = (root / name).read_bytes()
        if any(t.encode() in data or t.encode('utf-16-le') in data
               for t in ['DocxEngine.Experimental', 'ElixirOffice', 'ONLYOFFICE', 'Euro-Office']):
            raise ValueError('Référence au moteur écarté dans ' + name)
    return {'directory': str(root), 'projects': sorted(projects),
            'files': files, 'totalBytes': sum(f['bytes'] for f in files),
            'scope': 'Graphe, noms et signatures des assemblies propres. Ce contrôle ne remplace ni la provenance des sources ni l’audit des licences natives.'}


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--directory', required=True, type=Path)
    parser.add_argument('--report', type=Path)
    args = parser.parse_args()
    result = inspect(args.directory)
    if args.report:
        if args.report.resolve().is_relative_to(args.directory.resolve()):
            raise ValueError('Le rapport doit rester hors du paquet contrôlé')
        with args.report.open('x', encoding='utf-8') as output:
            json.dump(result, output, ensure_ascii=False, indent=2)
            output.write('\n')
    print('OK :', len(result['files']), 'fichiers ;', result['totalBytes'], 'octets ; projets C# :', ', '.join(result['projects']))


if __name__ == '__main__':
    main()
