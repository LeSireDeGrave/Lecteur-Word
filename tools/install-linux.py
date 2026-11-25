#!/usr/bin/env python3
"""Install a published Linux viewer for the current user. No root, download or MIME default change."""
import argparse
import datetime
import hashlib
import json
import os
from pathlib import Path
import re
import shlex
import shutil
import subprocess
import sys
import tempfile
import uuid

MANAGED = 'X-LecteurWord-Managed=true'
RELEASE_NAME = re.compile(r'[0-9]{8}T[0-9]{6}Z-[a-f0-9]{12}-[a-f0-9]{6}')


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


def launcher(root, follow_updates=False):
    executable = root / ('launch-latest' if follow_updates else 'current/LecteurWord')
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


def following_launcher(root, updates):
    return '\n'.join([
        '#!/bin/sh', '# ' + MANAGED,
        'candidate=' + shlex.quote(str(updates / 'latest')),
        'fallback=' + shlex.quote(str(root / 'current')),
        'if [ -x "$candidate/LecteurWord" ] && [ -f "$candidate/LecteurWord.dll" ] &&',
        '   ! [ "$fallback/LecteurWord.dll" -nt "$candidate/LecteurWord.dll" ] &&',
        '   cd -P -- "$candidate"; then',
        '    exec ./LecteurWord "$@"',
        'fi',
        'cd -P -- "$fallback" || exit 1',
        'exec ./LecteurWord "$@"',
        '',
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


def check_launcher(path, marker=MANAGED):
    if path.is_symlink():
        raise ValueError('Lanceur symbolique existant refusé : ' + str(path))
    if path.exists() and marker not in path.read_text(encoding='utf-8').splitlines():
        raise ValueError('Un lanceur différent existe ; il est préservé : ' + str(path))


def switch_version(pointer, release):
    pending = pointer.with_name('.' + pointer.name + '-' + uuid.uuid4().hex)
    try:
        pending.symlink_to(release.relative_to(pointer.parent), target_is_directory=True)
        os.replace(pending, pointer)
    finally:
        if pending.is_symlink():
            pending.unlink()


def copy_version(source, versions, audit=False):
    if source.is_relative_to(versions) or versions.is_relative_to(source):
        raise ValueError('Le dossier publié doit être distinct du dossier des versions.')
    versions.mkdir(parents=True, exist_ok=True)
    stage = Path(tempfile.mkdtemp(prefix='.stage-', dir=versions))
    try:
        shutil.copytree(source, stage, dirs_exist_ok=True, symlinks=True)
        paths = sorted(stage.rglob('*'))
        if any(p.is_symlink() for p in paths):
            raise ValueError('Les liens de la publication doivent être examinés avant installation.')
        if audit:
            checker = Path(__file__).with_name('check-independent-distribution.py')
            subprocess.run([sys.executable, str(checker), '--directory', str(stage)], check=True)
        digest = hashlib.sha256()
        for path in (p for p in paths if p.is_file()):
            digest.update(str(path.relative_to(stage)).encode('utf-8') + b'\0')
            with path.open('rb') as input_file:
                for chunk in iter(lambda: input_file.read(1024 * 1024), b''):
                    digest.update(chunk)
        timestamp = datetime.datetime.now(datetime.timezone.utc).strftime('%Y%m%dT%H%M%SZ')
        release = versions / f'{timestamp}-{digest.hexdigest()[:12]}-{uuid.uuid4().hex[:6]}'
        os.rename(stage, release)
        return release, digest.hexdigest()
    finally:
        if stage.exists():
            shutil.rmtree(stage)


def update_desktop_cache(applications):
    for command in [['update-desktop-database', str(applications)], ['kbuildsycoca6']]:
        if shutil.which(command[0]):
            try:
                result = subprocess.run(command, capture_output=True, text=True, timeout=45)
                if result.returncode:
                    print('Cache du bureau non actualisé : ' + command[0], file=sys.stderr)
            except subprocess.TimeoutExpired:
                print('Délai d’actualisation du cache dépassé : ' + command[0], file=sys.stderr)


def check_installation_paths(root):
    if root.is_symlink():
        raise ValueError('Dossier d’installation symbolique refusé.')
    marker, versions, current = (root / name for name in ['installation.json', 'versions', 'current'])
    if marker.is_symlink() or versions.is_symlink() or versions.exists() and not versions.is_dir():
        raise ValueError('Dossier de versions ou registre d’installation inattendu.')
    if current.is_symlink():
        if not current.resolve().is_relative_to(versions.resolve()):
            raise ValueError('Lien current différent préservé.')
    elif current.exists():
        raise ValueError('Chemin current existant non symbolique préservé.')
    return marker, versions, current


def uninstall(root, application_file):
    marker, versions, current = check_installation_paths(root)
    if not marker.is_file():
        raise ValueError('Installation utilisateur gérée introuvable.')
    plan = json.loads(marker.read_text(encoding='utf-8'))
    if plan.get('installation') != str(root) or plan.get('application') != str(application_file):
        raise ValueError('Registre d’installation incompatible.')
    launchers = [application_file]
    if saved := plan.get('desktop'):
        shortcut = Path(saved)
        if not shortcut.is_absolute() or shortcut.name != 'Lecteur Word.desktop':
            raise ValueError('Raccourci enregistré inattendu.')
        launchers.append(shortcut)
    for path in launchers:
        check_launcher(path)
    follow = root / 'launch-latest'
    check_launcher(follow, '# ' + MANAGED)
    releases = list(versions.iterdir()) if versions.is_dir() else []
    for release in releases:
        if not RELEASE_NAME.fullmatch(release.name) or release.is_symlink() or not release.is_dir():
            raise ValueError('Élément inattendu dans les versions, conservé : ' + str(release))
    for path in [*launchers, current, follow]:
        path.unlink(missing_ok=True)
    for release in releases:
        shutil.rmtree(release)
    if versions.is_dir():
        versions.rmdir()
    marker.unlink()
    update_desktop_cache(application_file.parent)
    print('Installation utilisateur supprimée. Préférences et extensions conservées.')


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--from', dest='source', type=Path)
    parser.add_argument('--data-home', type=Path)
    parser.add_argument('--desktop-dir', type=Path)
    parser.add_argument('--no-desktop-shortcut', action='store_true', help='Installer uniquement l’entrée du menu des applications.')
    parser.add_argument('--follow-updates', type=Path, help='Activer les publications locales pour le lanceur installé.')
    mode = parser.add_mutually_exclusive_group(required=True)
    mode.add_argument('--prepare-dir', type=Path, help='Préparer les fichiers reviewables sans installer.')
    mode.add_argument('--install', action='store_true')
    mode.add_argument('--uninstall', action='store_true')
    mode.add_argument('--stage-update', type=Path, help='Vérifier et rendre disponible une nouvelle publication locale.')
    args = parser.parse_args()
    if args.follow_updates is not None and not args.install:
        parser.error('--follow-updates exige --install')
    if args.desktop_dir is not None and args.no_desktop_shortcut:
        parser.error('--desktop-dir est incompatible avec --no-desktop-shortcut')
    home = Path.home()
    data = args.data_home or Path(os.environ.get('XDG_DATA_HOME') or str(home / '.local/share'))
    if not data.is_absolute():
        raise ValueError('XDG_DATA_HOME doit être absolu.')
    data = data.resolve()
    root = data / 'lecteur-word'
    applications = data / 'applications'
    application_file = applications / 'lecteur-word.desktop'
    if args.uninstall:
        uninstall(root, application_file)
        return
    if args.source is None:
        parser.error('--from est nécessaire pour préparer ou installer une version')
    source = args.source.resolve(strict=True)
    for name in ['LecteurWord', 'LecteurWord.dll', 'LecteurWord.runtimeconfig.json', 'desktop/lecteur-word.png']:
        if not (source / name).is_file():
            raise ValueError('Publication Linux incomplète : ' + name)
    if not os.access(source / 'LecteurWord', os.X_OK):
        raise ValueError('Le programme publié n’est pas exécutable.')
    if args.stage_update is not None:
        updates = args.stage_update.resolve(strict=True)
        if not (updates / 'enabled').is_file():
            raise ValueError('La mise à jour locale doit être activée.')
        release, _ = copy_version(source, updates / 'versions', audit=True)
        switch_version(updates / 'latest', release)
        print('Publication disponible depuis le lanceur : ' + str(release))
        return
    desktop = args.desktop_dir
    if not args.no_desktop_shortcut and desktop is None:
        result = subprocess.run(['xdg-user-dir', 'DESKTOP'], capture_output=True, text=True, check=True)
        desktop = Path(result.stdout.strip())
    if desktop is not None and (not desktop.is_absolute() or desktop.resolve() == home.resolve()):
        raise ValueError('Un dossier de bureau distinct et absolu est nécessaire (--desktop-dir).')
    desktop_file = desktop / 'Lecteur Word.desktop' if desktop is not None else None
    follow = root / 'launch-latest'
    follow_updates = args.install and (args.follow_updates is not None or follow.exists())
    content = launcher(root, follow_updates)
    plan = {'source': str(source), 'installation': str(root), 'application': str(application_file),
            'desktop': str(desktop_file) if desktop_file else None, 'executable': str(follow if follow_updates else root / 'current/LecteurWord'),
            'changesMimeDefaults': False, 'requiresRoot': False}
    if args.prepare_dir is not None:
        output = args.prepare_dir.resolve()
        output.mkdir(parents=True, exist_ok=True)
        atomic_text(output / 'lecteur-word.desktop', content, 0o755)
        atomic_text(output / 'installation.json', json.dumps(plan, ensure_ascii=False, indent=2) + '\n')
        print(json.dumps(plan, ensure_ascii=False, indent=2))
        return
    marker, versions, current = check_installation_paths(root)
    if not marker.is_file() and (versions.exists() or current.is_symlink() or follow.exists() or follow.is_symlink()):
        raise ValueError('Dossier de versions existant non géré préservé.')
    old_shortcut = None
    if marker.is_file():
        saved_plan = json.loads(marker.read_text(encoding='utf-8'))
        if saved_plan.get('installation') != str(root) or saved_plan.get('application') != str(application_file):
            raise ValueError('Registre d’installation incompatible.')
        if previous := saved_plan.get('desktop'):
            old_shortcut = Path(previous)
            if not old_shortcut.is_absolute() or old_shortcut.name != 'Lecteur Word.desktop':
                raise ValueError('Raccourci enregistré inattendu.')
    for path in {application_file, desktop_file, old_shortcut} - {None}:
        check_launcher(path)
    updates = None
    if follow_updates:
        check_launcher(follow, '# ' + MANAGED)
        if args.follow_updates is not None:
            updates = args.follow_updates.resolve(strict=True)
            if not (updates / 'enabled').is_file():
                raise ValueError('La mise à jour locale doit être activée.')
    root.mkdir(mode=0o700, parents=True, exist_ok=True)
    release, digest = copy_version(source, versions)
    switch_version(current, release)
    if updates is not None:
        atomic_text(follow, following_launcher(root, updates), 0o755)
    atomic_text(application_file, content, 0o755)
    if desktop_file is not None:
        atomic_text(desktop_file, content, 0o755)
    if old_shortcut is not None and old_shortcut != desktop_file:
        old_shortcut.unlink(missing_ok=True)
    plan.update(release=str(release), releaseSha256=digest)
    atomic_text(marker, json.dumps(plan, ensure_ascii=False, indent=2) + '\n', 0o600)
    update_desktop_cache(applications)
    print(json.dumps(plan, ensure_ascii=False, indent=2))


if __name__ == '__main__':
    try:
        main()
    except (OSError, ValueError, subprocess.SubprocessError) as error:
        print('Installation refusée ou incomplète : ' + str(error), file=sys.stderr)
        sys.exit(1)
