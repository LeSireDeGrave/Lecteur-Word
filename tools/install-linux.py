#!/usr/bin/env python3
"""Install a published Linux viewer for the current user. No root, download or MIME default change."""
import argparse
import datetime
import hashlib
import json
import os
from pathlib import Path
import shutil
import subprocess
import sys
import tempfile
import uuid

MANAGED = 'X-LecteurWord-Managed=true'


def desktop_value(value):
    value = str(value)
    if any(c in value for c in '\0\r\n'):
        raise ValueError('Un chemin de lanceur contient un caractère interdit.')
    return value.replace('\\', '\\\\').replace('\t', '\\t')


def exec_argument(value):
    value = str(value)
    if '=' in value or '%' in value:
        raise ValueError('Chemin d’exécutable avec = ou % non qualifié pour les lanceurs Linux ; choisir un autre dossier de données.')
    quoted = ''.join('\\' + c if c in '\\"`$' else c for c in value).replace('%', '%%')
    return desktop_value('"' + quoted + '"')


def launcher(root):
    executable = root / 'current/LecteurWord'
    return '\n'.join([
        '[Desktop Entry]', 'Type=Application', 'Name=Lecteur Word',
        'GenericName=Lecteur de documents Word', 'GenericName[en]=Word document reader',
        'Comment=Lire les documents DOCX avec Lecteur Word',
        'Comment[en]=Read DOCX documents with Lecteur Word',
        'Exec=' + exec_argument(executable) + ' %f',
        'TryExec=' + desktop_value(executable),
        'Icon=' + desktop_value(root / 'current/desktop/lecteur-word.png'),
        'Path=' + desktop_value(root / 'current'),
        'Terminal=false', 'StartupNotify=false', 'StartupWMClass=LecteurWord',
        'Categories=Office;Viewer;',
        'MimeType=application/vnd.openxmlformats-officedocument.wordprocessingml.document;',
        'Keywords=Word;DOCX;Lecture;', MANAGED, '',
    ])


def atomic_text(path, content, mode=0o644):
    path.parent.mkdir(parents=True, exist_ok=True)
    fd, temporary = tempfile.mkstemp(prefix='.' + path.name + '-', dir=path.parent)
    try:
        with os.fdopen(fd, 'w', encoding='utf-8') as output:
            output.write(content)
            output.flush()
            os.fsync(output.fileno())
        os.chmod(temporary, mode)
        os.replace(temporary, path)
    finally:
        if os.path.exists(temporary):
            os.unlink(temporary)


def check_launcher(path):
    if path.is_symlink():
        raise ValueError('Lanceur symbolique existant refusé : ' + str(path))
    if path.exists() and MANAGED not in path.read_text(encoding='utf-8').splitlines():
        raise ValueError('Un lanceur différent existe ; il est préservé : ' + str(path))


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--from', dest='source', required=True, type=Path)
    parser.add_argument('--data-home', type=Path)
    parser.add_argument('--desktop-dir', type=Path)
    mode = parser.add_mutually_exclusive_group(required=True)
    mode.add_argument('--prepare-dir', type=Path, help='Préparer les fichiers reviewables sans installer.')
    mode.add_argument('--install', action='store_true')
    args = parser.parse_args()
    home = Path.home()
    data = args.data_home or Path(os.environ.get('XDG_DATA_HOME', str(home / '.local/share')))
    if not data.is_absolute():
        raise ValueError('XDG_DATA_HOME doit être absolu.')
    desktop = args.desktop_dir
    if desktop is None:
        result = subprocess.run(['xdg-user-dir', 'DESKTOP'], capture_output=True, text=True, check=True)
        desktop = Path(result.stdout.strip())
    if not desktop.is_absolute() or desktop == home:
        raise ValueError('Un dossier de bureau distinct et absolu est nécessaire (--desktop-dir).')
    source = args.source.resolve(strict=True)
    for name in ['LecteurWord', 'LecteurWord.dll', 'LecteurWord.runtimeconfig.json', 'desktop/lecteur-word.png']:
        if not (source / name).is_file():
            raise ValueError('Publication Linux incomplète : ' + name)
    if not os.access(source / 'LecteurWord', os.X_OK):
        raise ValueError('Le programme publié n’est pas exécutable.')
    root = data / 'lecteur-word'
    applications = data / 'applications'
    application_file = applications / 'lecteur-word.desktop'
    desktop_file = desktop / 'Lecteur Word.desktop'
    content = launcher(root)
    plan = {'source': str(source), 'installation': str(root), 'application': str(application_file),
            'desktop': str(desktop_file), 'executable': str(root / 'current/LecteurWord'),
            'changesMimeDefaults': False, 'requiresRoot': False}
    if args.prepare_dir is not None:
        output = args.prepare_dir.resolve()
        output.mkdir(parents=True, exist_ok=True)
        atomic_text(output / 'lecteur-word.desktop', content, 0o755)
        atomic_text(output / 'installation.json', json.dumps(plan, ensure_ascii=False, indent=2) + '\n')
        print(json.dumps(plan, ensure_ascii=False, indent=2))
        return
    for path in [application_file, desktop_file]:
        check_launcher(path)
    if root.is_symlink():
        raise ValueError('Dossier d’installation symbolique refusé.')
    if root.exists() and any(root.iterdir()) and not (root / 'installation.json').is_file():
        raise ValueError('Dossier existant non géré préservé : ' + str(root))
    current = root / 'current'
    if current.is_symlink():
        if not current.resolve().is_relative_to((root / 'versions').resolve()):
            raise ValueError('Lien current différent préservé.')
    elif current.exists():
        raise ValueError('Chemin current existant non symbolique préservé.')
    files = sorted(p for p in source.rglob('*') if p.is_file())
    if any(p.is_symlink() for p in source.rglob('*')):
        raise ValueError('Les liens de la publication doivent être examinés avant installation.')
    digest = hashlib.sha256()
    for path in files:
        digest.update(str(path.relative_to(source)).encode('utf-8') + b'\0')
        with path.open('rb') as input_file:
            for chunk in iter(lambda: input_file.read(1024 * 1024), b''):
                digest.update(chunk)
    release_id = datetime.datetime.now(datetime.timezone.utc).strftime('%Y%m%dT%H%M%SZ') + '-' + digest.hexdigest()[:12] + '-' + uuid.uuid4().hex[:6]
    versions = root / 'versions'
    versions.mkdir(parents=True, exist_ok=True)
    stage = Path(tempfile.mkdtemp(prefix='.stage-', dir=versions))
    release = versions / release_id
    try:
        shutil.copytree(source, stage, dirs_exist_ok=True)
        os.rename(stage, release)
    finally:
        if stage.exists():
            shutil.rmtree(stage)  # Only this invocation's private, fresh staging directory.
    next_link = root / ('.current-' + uuid.uuid4().hex)
    try:
        next_link.symlink_to(Path('versions') / release_id, target_is_directory=True)
        os.replace(next_link, current)
    finally:
        if next_link.is_symlink():
            next_link.unlink()
    atomic_text(application_file, content, 0o755)
    atomic_text(desktop_file, content, 0o755)
    plan.update(release=str(release), releaseSha256=digest.hexdigest())
    atomic_text(root / 'installation.json', json.dumps(plan, ensure_ascii=False, indent=2) + '\n')
    # Optional user desktop caches; never change the selected default DOCX editor.
    for command in [['update-desktop-database', str(applications)], ['kbuildsycoca6']]:
        if shutil.which(command[0]):
            result = subprocess.run(command, capture_output=True, text=True, timeout=45)
            if result.returncode:
                print('Cache du bureau non actualisé : ' + command[0], file=sys.stderr)
    print(json.dumps(plan, ensure_ascii=False, indent=2))


if __name__ == '__main__':
    try:
        main()
    except (OSError, ValueError, subprocess.SubprocessError) as error:
        print('Installation refusée ou incomplète : ' + str(error), file=sys.stderr)
        sys.exit(1)
