"""Assemble only the GT7 package allowlist, then call its own packager."""
import argparse
import hashlib
import importlib.util
import json
import os
import re
import shutil
import sys
import zipfile
from import_audit import audit
from renderer_provenance import verify as verify_renderer
from pathlib import Path, PurePosixPath

# FEAT-GTS-253: AMD's FidelityFX runtime, the one packaged file not built here (pinned with the toolchain).
FIDELITYFX = json.loads(Path(__file__).with_name('runtime-toolchain.json').read_text(encoding='utf-8-sig'))['fidelityfx']
FIDELITYFX_FILES = (('amd_fidelityfx_vk.dll', 'dll_sha256'), ('amd_fidelityfx_vk-LICENSE.txt', 'license_sha256'))
GCC_DLLS = ('libgcc_s_seh-1.dll', 'libstdc++-6.dll', 'libwinpthread-1.dll')
RENDERER_LIBS = ('libSceVideoOut.prx', 'libSceAudio3d.prx', 'libSceAjm.prx',
                 'libSceNgs2.prx', 'libSceZlib.prx', 'libSceDiscMap.prx',
                 'libSceScreenShot.prx', 'libSceNpAuth.prx')
STUB_LIBS = ('libSceTextToSpeech.prx', 'libSceMbus.prx')
REQUIRED_RUNTIME = ('libc.prx', 'libkernel.prx', 'libSceFiber.prx', 'libScePad.prx',
                    'libSceHttp.prx', 'libSceNet.prx', 'libSceNetCtl.prx', 'libSceNpManager.prx',
                    'libSceNpSessionSignaling.prx', 'libSceRtc.prx', 'libSceSsl.prx')


def launcher_version(source_root):
    """LauncherVersion.Value from loader/Version.cs, or None when the source has no such file."""
    path = Path(source_root) / 'loader/Version.cs'
    if not path.is_file():
        return None
    match = re.search(r'public const string Value = "([^"]+)";', path.read_text(encoding='utf-8-sig'))
    return match.group(1) if match else None

def sha(path):
    with Path(path).open('rb') as f:
        return hashlib.file_digest(f, 'sha256').hexdigest()

def one_file(folder, name):
    found = [p for p in Path(folder).rglob('*') if p.is_file() and p.name.lower() == name.lower()]
    if len(found) != 1:
        raise ValueError(f'expected one {name} under {folder}, found {len(found)}')
    return found[0]

def assemble(args):
    names = [n.strip() for n in (args.source / 'build-support/runtime-files.txt').read_text().splitlines()
             if n.strip() and not n.startswith('#')]
    if len({n.lower() for n in names}) != len(names) or any(Path(n).name != n or '/' in n or '\\' in n for n in names):
        raise ValueError('invalid or duplicate allowlist entry')
    args.runtime.mkdir(parents=True, exist_ok=False)
    libs = args.runtime / 'libs'; libs.mkdir()
    origins = {}
    # FEAT-GTS-253: every allowlisted file is built from this revision (runtime, renderer, GT7 stubs) or
    # comes from the pinned toolchain; there is no runtime archive to fall back on.
    renderer = {n.lower() for n in RENDERER_LIBS}
    stubs = {n.lower() for n in STUB_LIBS}
    gcc = {n.lower() for n in GCC_DLLS}
    allow = {n.lower(): n for n in names}
    for name in RENDERER_LIBS + STUB_LIBS:
        if name.lower() not in allow: raise ValueError(f'build output absent from allowlist: {name}')
    # A clean build directory is required; only exact allowlisted names are considered.
    built = {item.name.lower(): item for item in args.built_libs.iterdir() if item.is_file()}
    for name in names:
        key = name.lower()
        if key in renderer:
            shutil.copyfile(one_file(args.renderer_build, name), libs / name)
            origins['libs/' + name] = {'origin': 'shadgt7-source-build'}
        elif key in stubs:
            shutil.copyfile(one_file(args.stubs, name), libs / name)
            origins['libs/' + name] = {'origin': 'rygt7-gt7-stub-build'}
        elif key in gcc:
            shutil.copyfile(args.ucrt / 'bin' / name, libs / name)
            origins['libs/' + name] = {'origin': 'ucrt64-toolchain'}
        elif key in built:
            shutil.copyfile(built[key], libs / name)
            origins['libs/' + name] = {'origin': 'rygt7-source-build'}
        else:
            raise ValueError(f'allowlisted runtime file was not built: {name}')
    for name in REQUIRED_RUNTIME:
        if name.lower() not in allow or origins['libs/' + allow[name.lower()]]['origin'] != 'rygt7-source-build':
            raise ValueError(f'required fresh runtime output missing: {name}')
    for name in GCC_DLLS:
        shutil.copyfile(args.ucrt / 'bin' / name, args.runtime / name); origins[name] = {'origin': 'ucrt64-toolchain'}
    for name, key in FIDELITYFX_FILES:
        if sha(args.fidelityfx / name) != FIDELITYFX[key]:
            raise ValueError(f'{name} does not match the pinned {FIDELITYFX["version"]}')
        shutil.copyfile(args.fidelityfx / name, args.runtime / name)
        origins[name] = {'origin': 'amd-fidelityfx-sdk', 'version': FIDELITYFX['version']}
    for name, metadata in origins.items():
        metadata['sha256'] = sha(args.runtime / name)
    info = {'schema': 1, 'rygt7_commit': args.source_sha, 'renderer_commit': args.renderer_sha,
            'renderer_configured_url': args.renderer_configured_url, 'renderer_fetch_url': args.renderer_fetch_url,
            'runtime_files': origins,
            'launcher_version': launcher_version(args.source)}
    control = {'repository': os.environ.get('GITHUB_REPOSITORY'), 'commit': os.environ.get('GITHUB_SHA'),
               'attempt': os.environ.get('GITHUB_RUN_ATTEMPT'),
               'run_url': (f"https://github.com/{os.environ['GITHUB_REPOSITORY']}/actions/runs/{os.environ['GITHUB_RUN_ID']}"
                           if os.environ.get('GITHUB_REPOSITORY') and os.environ.get('GITHUB_RUN_ID') else None)}
    info['ci_control'] = control
    if getattr(args, 'toolchain_info', None):
        info['toolchain'] = json.loads(args.toolchain_info.read_text(encoding='utf-8-sig'))
    info['components'] = {name: {'sha256': sha(path), 'origin': origin} for name, path, origin in (
        ('launcher', args.launcher, 'rygt7-source-build'), ('converter', args.converter, 'rygt7-source-build'),
        ('wheel_probe', args.wheel_probe, 'rygt7-source-build'))}
    args.info.write_text(json.dumps(info, indent=2) + '\n', encoding='utf-8')

def package(args):
    spec = importlib.util.spec_from_file_location('gt7_packager', args.source / 'build-support/package_release.py')
    module = importlib.util.module_from_spec(spec); spec.loader.exec_module(module)
    module.PROVENANCE = 'Per-file provenance and hashes are recorded in BUILD_INFO.json; every file in this CI package is built from source at the recorded revisions, except the pinned MinGW runtime DLLs and AMD FidelityFX runtime.'
    sys.argv = ['package_release.py', '--runtime', str(args.runtime), '--launcher', str(args.launcher),
                '--converter', str(args.converter), '--renderer-source', str(args.renderer_source),
                '--renderer-deps-source', str(getattr(args, 'renderer_deps_source', args.renderer_source / 'externals')),
                '--wheel-probe', str(args.wheel_probe), '--gcc-licenses', str(args.ucrt / 'share/licenses'),
                '--deps-source', str(getattr(args, 'deps_source', args.source / '3rdparty')),
                '--out', str(args.out), '--name', args.name]
    module.main()
    folder = args.out / args.name
    shutil.copyfile(args.info, folder / 'BUILD_INFO.json')
    # The release template describes historical gameplay, not this CI build.
    readme = folder / 'README.txt'
    readme.write_text('RyGT7 source CI build. No game, firmware or player data is included.\n'
                      'This CI artifact has not been validated in game.\n'
                      'See BUILD_INFO.json for per-file provenance and docs/launcher.md for setup.\n', encoding='utf-8')
    manifest_path = folder / 'manifest.json'; manifest = json.loads(manifest_path.read_text(encoding='utf-8'))
    for record in manifest['files']:
        if record['path'] == 'README.txt':
            record['bytes'] = readme.stat().st_size; record['sha256'] = sha(readme)
            break
    else: raise ValueError('packager omitted README.txt')
    info = folder / 'BUILD_INFO.json'
    manifest['files'].append({'path': 'BUILD_INFO.json', 'bytes': info.stat().st_size, 'sha256': sha(info)})
    manifest['files'].sort(key=lambda row: row['path'])
    manifest_path.write_text(json.dumps(manifest, indent=1), encoding='utf-8')
    archive = args.out / (args.name + '.zip')
    with zipfile.ZipFile(archive, 'w', zipfile.ZIP_DEFLATED, compresslevel=6) as z:
        for file in sorted(folder.rglob('*')):
            if file.is_file(): z.write(file, (Path(args.name) / file.relative_to(folder)).as_posix())
    digest = sha(archive); archive.with_suffix('.zip.sha256').write_text(f'{digest}  {archive.name}\n', encoding='ascii')
    marker = {'schema': 1, 'project': 'rygt-team/rygt7', 'branch': 'main', 'commit': args.source_sha,
              'renderer': {'configured_repository': args.renderer_configured_url,
                           'fetched_repository': args.renderer_fetch_url, 'commit': args.renderer_sha},
              'archive': {'name': archive.name, 'sha256': digest}, 'build_info': 'BUILD_INFO.json',
              'ci_control': json.loads(args.info.read_text(encoding='utf-8'))['ci_control']}
    (args.out / 'CI-BUILD.json').write_text(json.dumps(marker, indent=2) + '\n', encoding='utf-8')
    print(f'{archive} SHA256 {digest}')

def main():
    p = argparse.ArgumentParser()
    for name in ('source','fidelityfx','runtime','built-libs','renderer-build','stubs','ucrt','info','launcher','converter','renderer-source','wheel-probe','out'):
        p.add_argument('--' + name, type=Path, required=True)
    p.add_argument('--toolchain-info', type=Path)
    for name in ('source-sha','renderer-sha','renderer-configured-url','renderer-fetch-url','name'):
        p.add_argument('--' + name, required=True)
    args = p.parse_args()
    if not re.fullmatch(r'[0-9a-f]{40}', args.source_sha): raise ValueError('invalid source SHA')
    if not re.fullmatch(r'[0-9a-f]{40}', args.renderer_sha) or args.renderer_sha != os.environ.get('RENDERER_SHA'):
        raise ValueError('renderer SHA differs from resolved CI revision')
    if args.name != 'RyGT7-ci-' + args.source_sha[:8]: raise ValueError('unexpected package name')
    assemble(args)
    info = json.loads(args.info.read_text(encoding="utf-8"))
    info["static_pe_import_audit"] = audit(args.runtime, args.converter, args.launcher)
    info["renderer_vma_patch"] = verify_renderer(args.renderer_source)
    args.info.write_text(json.dumps(info, indent=2) + "\n", encoding="utf-8")
    package(args)

if __name__ == '__main__': main()
