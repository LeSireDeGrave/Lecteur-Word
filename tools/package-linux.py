#!/usr/bin/env python3
"""Assemble local Linux x64 packages from two inspected self-contained publications."""

import argparse
import hashlib
import json
import os
from pathlib import Path
import re
import shutil
import subprocess
import sys
import tarfile
import tempfile


ROOT = Path(__file__).resolve().parents[1]
PACKAGING = ROOT / 'packaging/linux'
DOCS = ROOT / 'docs'
VIEWER = 'LecteurWord'
CLI = 'lecteur-word-cli'
SLUG = 'lecteur-word'
DOC_DIRECTORY = Path('usr/share/doc') / SLUG
APP_DIRECTORY = Path('usr/lib') / SLUG
DEB_DEPENDS = (
    'ca-certificates, libc6 (>= 2.28), libbrotli1, libgcc-s1, libgssapi-krb5-2, '
    'libicu74 | libicu76 | libicu77 | libicu78, libssl3t64 | libssl3, libstdc++6, tzdata, zlib1g, '
    'libfontconfig1, libx11-6, libice6, libsm6, libxcursor1, libxi6, libxrandr2, libxinerama1, libxext6, '
    'libgl1, libegl1, desktop-file-utils, shared-mime-info'
)


def run(*arguments, env=None):
    subprocess.run(arguments, check=True, env=env)


def sha256(path):
    with path.open('rb') as file:
        return hashlib.file_digest(file, 'sha256').hexdigest()


def write_json(path, value):
    path.write_text(json.dumps(value, ensure_ascii=False, indent=2) + '\n', encoding='utf-8')


def build_info(binary, option):
    env = {name: value for name, value in os.environ.items()
           if name not in ('DISPLAY', 'WAYLAND_DISPLAY', 'DBUS_SESSION_BUS_ADDRESS', 'XAUTHORITY')}
    return json.loads(subprocess.check_output((binary, option), env=env))


def graph(directory, binary):
    deps = json.loads((directory / (binary + '.deps.json')).read_text())
    return {name: deps['libraries'][name]['type']
            for name, assets in deps['targets'][deps['runtimeTarget']['name']].items()
            if any(kind in assets for kind in ('runtime', 'native', 'runtimeTargets'))}


def copy_distribution(source, target):
    target.mkdir(parents=True, exist_ok=True)
    for path in sorted(source.rglob('*')):
        relative = path.relative_to(source)
        if path.is_symlink() or not (path.is_file() or path.is_dir()):
            raise ValueError('Unexpected payload entry: ' + str(relative))
        if path.suffix in ('.pdb', '.dmp'):
            continue
        destination = target / relative
        if path.is_dir():
            destination.mkdir(mode=0o755, exist_ok=True)
        else:
            shutil.copyfile(path, destination)
            destination.chmod(0o755 if path.stat().st_mode & 0o111 else 0o644)


def documentation(destination, identity, packages, gui):
    destination.mkdir(parents=True, exist_ok=True)
    for source, name in (
        (ROOT / 'LICENSE', 'LICENSE'),
        (ROOT / 'BRANDING.md', 'BRANDING.md'),
        (PACKAGING / 'copyright', 'copyright'),
        (DOCS / 'guide-utilisateur.fr.md', 'README.fr.md'),
        (DOCS / 'user-guide.en.md', 'README.en.md'),
        (DOCS / 'historique-versions.md', 'CHANGELOG.md'),
    ):
        shutil.copyfile(source, destination / name)
    if not gui:
        shutil.copyfile(DOCS / 'lecteur-word-cli.md', destination / 'CLI-CONTRACT.md')
    notices = ROOT / 'src/LecteurWord/Notices'
    target_notices = destination / 'notices'
    target_notices.mkdir()
    for notice in sorted(notices.glob('*.txt')):
        if gui or notice.name[:2] not in ('07', '09'):
            shutil.copyfile(notice, target_notices / notice.name)
    write_json(destination / 'BUILD.json', identity)
    write_json(destination / 'DEPENDENCIES.json', {
        'applicationLicense': 'MIT',
        'runtime': identity['framework'],
        'runtimeAssets': packages,
        'notices': 'notices/',
        'distribution': 'self-contained linux-x64 (glibc)',
        'hostLibraries': ['glibc', 'libgcc', 'libstdc++', 'ICU', 'OpenSSL', 'zlib', 'GSSAPI', 'Fontconfig']
                         + (['X11', 'ICE', 'SM', 'Xcursor', 'Xi', 'Xrandr', 'Xinerama', 'Xext', 'OpenGL/EGL'] if gui else []),
    })


def system_tree(viewer, destination, identity, packages):
    copy_distribution(viewer, destination / APP_DIRECTORY)
    bindir = destination / 'usr/bin'
    bindir.mkdir(parents=True)
    # The apphost resolves the installed binary and its adjacent runtime files.
    (bindir / SLUG).symlink_to('../lib/' + SLUG + '/' + VIEWER)
    applications = destination / 'usr/share/applications'
    applications.mkdir(parents=True)
    shutil.copyfile(PACKAGING / 'lecteur-word.desktop', applications / 'lecteur-word.desktop')
    icon = destination / 'usr/share/icons/hicolor/256x256/apps/lecteur-word.png'
    icon.parent.mkdir(parents=True)
    shutil.copyfile(viewer / 'desktop/lecteur-word.png', icon)
    documentation(destination / DOC_DIRECTORY, identity, packages, gui=True)


def tarball(source, destination):
    def normalize(entry):
        entry.uid = entry.gid = 0
        entry.uname = entry.gname = 'root'
        entry.mode = 0o755 if entry.isdir() or entry.mode & 0o111 else 0o644
        return entry

    with tarfile.open(destination, 'x:gz', compresslevel=6) as tar:
        tar.add(source, arcname=source.name, filter=normalize)


def debian(system, work, artifact, version):
    staging = work / 'deb'
    shutil.copytree(system, staging, symlinks=True)
    control = staging / 'DEBIAN/control'
    control.parent.mkdir()
    installed_size = sum(p.stat().st_size for p in system.rglob('*') if p.is_file() and not p.is_symlink())
    control.write_text(
        f'Package: {SLUG}\nVersion: {version}\nArchitecture: amd64\nSection: editors\nPriority: optional\n'
        f'Maintainer: LeSireDeGrave\nInstalled-Size: {(installed_size + 1023) // 1024}\nDepends: {DEB_DEPENDS}\n'
        'Description: Read and export DOCX documents\n'
        ' Read DOCX documents and export to PDF, ODT, EPUB 3 and NOVA.\n'
        ' The application ships with its .NET runtime.\n', encoding='utf-8')
    run('dpkg-deb', '-Zzstd', '-z6', '--threads-max=2', '--root-owner-group', '--build', staging, artifact)


def rpm(system, artifact, version, release):
    # RPM passes some generated script paths unquoted to /bin/sh.
    with tempfile.TemporaryDirectory(prefix='lw-rpm-', dir='/tmp') as temporary:
        rpm_tree = Path(temporary)
        sources = rpm_tree / 'SOURCES'
        sources.mkdir()
        temp = rpm_tree / 'tmp'
        temp.mkdir()
        shutil.copytree(system, sources / 'package', symlinks=True)
        env = os.environ.copy()
        if config := env.get('RPM_CONFIGDIR'):
            config_link = rpm_tree / 'rpm-config'
            config_link.symlink_to(Path(config).resolve(strict=True), target_is_directory=True)
            env['RPM_CONFIGDIR'] = str(config_link)
        run('rpmbuild', '-bb', '--nodeps', '--target', 'x86_64', '--define', f'_topdir {rpm_tree}',
            '--define', f'_tmppath {temp}', '--define', f'_dbpath {rpm_tree / "db"}',
            '--define', '_binary_payload w9.zstdio',
            '--define', f'lw_version {version}', '--define', f'lw_release {release}', PACKAGING / 'lecteur-word.spec',
            env=env)
        built, = (rpm_tree / 'RPMS').glob('*/*.rpm')
        shutil.move(built, artifact)


def appimage(system, work, artifact, tool, runtime, legal):
    staging = work / 'LecteurWord.AppDir'
    shutil.copytree(system, staging, symlinks=True)
    shutil.copyfile(PACKAGING / 'AppRun', staging / 'AppRun')
    (staging / 'AppRun').chmod(0o755)
    shutil.copyfile(PACKAGING / 'lecteur-word.desktop', staging / 'lecteur-word.desktop')
    shutil.copyfile(staging / 'usr/share/icons/hicolor/256x256/apps/lecteur-word.png', staging / 'lecteur-word.png')
    (staging / '.DirIcon').symlink_to('lecteur-word.png')
    install_dir = staging / 'usr/share/lecteur-word'
    install_dir.mkdir()
    shutil.copyfile(ROOT / 'tools/install-linux.py', install_dir / 'install-linux.py')
    copy_distribution(legal, staging / DOC_DIRECTORY / 'notices/appimage')
    env = {**os.environ, 'ARCH': 'x86_64', 'APPIMAGE_EXTRACT_AND_RUN': '1'}
    run(tool, '--no-appstream', '--runtime-file', runtime, '--comp', 'zstd',
        '--mksquashfs-opt=-processors', '--mksquashfs-opt=2', staging, artifact, env=env)


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    for name in ('viewer', 'cli', 'output', 'appimagetool', 'appimage-runtime', 'appimage-notices'):
        parser.add_argument('--' + name, required=True, type=Path)
    parser.add_argument('--appimage-sources', required=True, type=Path)
    parser.add_argument('--appimage-runtime-sha256', required=True)
    parser.add_argument('--revision', default=1, type=int, help='Local package revision, independent of application SemVer')
    args = parser.parse_args()
    if args.revision < 1 or args.revision > 999999:
        parser.error('--revision must be between 1 and 999999')
    viewer, cli, output = (path.resolve() for path in (args.viewer, args.cli, args.output))
    if output.exists() or any(output.is_relative_to(path) for path in (viewer, cli)):
        parser.error('--output must be a fresh directory outside the input publications')
    runtime = args.appimage_runtime.resolve(strict=True)
    if sha256(runtime) != args.appimage_runtime_sha256.lower():
        raise ValueError('AppImage runtime SHA-256 mismatch')
    legal = args.appimage_notices.resolve(strict=True)
    if not (legal / 'SOURCE.txt').is_file():
        raise ValueError('AppImage component notices must include SOURCE.txt')
    sources = args.appimage_sources.resolve(strict=True)
    if not (sources / 'SHA256SUMS').is_file():
        raise ValueError('AppImage source companion must include SHA256SUMS')
    subprocess.run(('sha256sum', '-c', 'SHA256SUMS'), cwd=sources, check=True)
    runtime_source = json.loads((sources / 'RUNTIME.json').read_text())
    if runtime_source['runtimeSha256'] != args.appimage_runtime_sha256.lower():
        raise ValueError('AppImage source companion identifies a different runtime')
    if (sources / 'SOURCE.txt').read_bytes() != (legal / 'SOURCE.txt').read_bytes():
        raise ValueError('AppImage source notices disagree with the companion archive')
    run(sys.executable, ROOT / 'tools/check-independent-distribution.py', '--directory', viewer)
    gui_info = build_info(viewer / VIEWER, '--build-info')
    cli_info = build_info(cli / CLI, 'capabilities')
    cli_build = cli_info['payload']['build']
    fields = ('applicationVersion', 'engineVersion', 'commit', 'hasLocalChanges', 'target', 'framework',
              'cliContractVersion', 'extensionApiVersion', 'novaVersions')
    if any(gui_info[key] != cli_build[key] for key in fields) or gui_info['target'] != 'linux-x64':
        raise ValueError('Viewer and CLI builds do not match')
    version = gui_info['applicationVersion']
    number = r'(?:0|[1-9][0-9]*)'
    token = r'(?:0|[1-9][0-9]*|[0-9]*[A-Za-z-][0-9A-Za-z-]*)'
    allowed = rf'{number}\.{number}\.{number}(?:-{token}(?:\.{token})*)?'
    if not isinstance(version, str) or not re.fullmatch(allowed, version):
        raise ValueError('Expected major.minor.patch with an optional SemVer prerelease')
    upstream, marker, prerelease = version.partition('-')
    system_version = upstream + ('~' + prerelease if marker else '')
    gui_graph, cli_graph = graph(viewer, VIEWER), graph(cli, CLI)
    if any('avalonia' in key.lower() for key in cli_graph) or any('avalonia' in p.name.lower() for p in cli.rglob('*')):
        raise ValueError('The standalone CLI contains Avalonia')
    app_version = (gui_info['commit'] or 'unknown')[:8] + ('.dirty' if gui_info['hasLocalChanges'] else '')
    revision = f'0.{args.revision}.local.{app_version}'
    stem = f'{SLUG}-{version}-{revision}'
    gui_tar = f'{stem}-linux-x64.tar.gz'
    cli_tar = f'{CLI}-{version}-{revision}-linux-x64.tar.gz'
    deb_name = f'{SLUG}_{system_version}-{revision}_amd64.deb'
    rpm_name = f'{stem}.x86_64.rpm'
    image_name = f'LecteurWord-{version}-{revision}-x86_64.AppImage'
    output.parent.mkdir(parents=True, exist_ok=True)
    with tempfile.TemporaryDirectory(prefix='lw-packages-', dir=output.parent) as temporary:
        work = Path(temporary)
        packages = work / 'delivery'
        packages.mkdir()
        system = work / 'system'
        system_tree(viewer, system, gui_info, gui_graph)
        portable = work / 'lecteur-word-linux-x64'
        copy_distribution(viewer, portable / 'app')
        documentation(portable, gui_info, gui_graph, gui=True)
        shutil.copyfile(ROOT / 'tools/install-linux.py', portable / 'install-linux.py')
        standalone = work / 'lecteur-word-cli-linux-x64'
        copy_distribution(cli, standalone)
        documentation(standalone, cli_build, cli_graph, gui=False)
        run('desktop-file-validate', system / 'usr/share/applications/lecteur-word.desktop')
        tarball(portable, packages / gui_tar)
        tarball(standalone, packages / cli_tar)
        debian(system, work, packages / deb_name, f'{system_version}-{revision}')
        rpm(system, packages / rpm_name, system_version, revision)
        appimage(system, work, packages / image_name, args.appimagetool.resolve(strict=True), runtime, legal)
        tarball(sources, packages / ('AppImage-runtime-sources-' + revision + '.tar.gz'))
        files = sorted(packages.iterdir())
        write_json(packages / 'RELEASE.json', {
            'status': 'local-preparation',
            'applicationLicense': 'MIT',
            'viewer': gui_info, 'cli': cli_build,
            'appImageRuntimeSha256': args.appimage_runtime_sha256.lower(),
            'packages': [{'name': path.name, 'bytes': path.stat().st_size, 'sha256': sha256(path)} for path in files],
        })
        (packages / 'SHA256SUMS').write_text(''.join(
            f'{sha256(path)}  {path.name}\n' for path in sorted(packages.iterdir())), encoding='utf-8')
        packages.rename(output)
    print(output)


if __name__ == '__main__':
    main()
