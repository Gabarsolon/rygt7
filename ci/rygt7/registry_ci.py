"""Resolve trusted GT7 source and validate the complete CI artifact without extracting it."""
import sys
sys.path.insert(0, str(__import__("pathlib").Path(__file__).resolve().parent))
from github_source import resolve_commit
import hashlib
import json
import os
from pathlib import Path, PurePosixPath
import re
import stat
import sys
import urllib.request
import urllib.error
import zipfile

API = 'https://api.github.com/repos/RyGT-Team/rygt7'
RENDERER_API = 'https://api.github.com/repos/RyGT-Team/shadgt7'
RENDERER = os.environ.get('RENDERER_SHA', '')
BASE = 'd8efb3c45b9eb164a66330d60e68638ff3d2a2a74fd51680482e989791697d2e'
BASE_URL = 'https://gitlab.com/api/v4/projects/88084032/packages/generic/rygt7/2026-10-31/RyGT7-2026-10-31-prerelease.zip'
CONFIGURED = 'https://github.com/RyGT-Team/shadgt7.git'
FETCHED = 'https://github.com/RyGT-Team/shadgt7.git'
SHA = re.compile(r'^[0-9a-f]{40}$')
DIGEST = re.compile(r'^[0-9a-f]{64}$')
REQUIRED = {'RyGT7.exe', 'converter/ryty.exe', 'libs/libSceVideoOut.prx', 'libs/libSceFiber.prx', 'BUILD_INFO.json'}
BAD = {'game', 'games', 'firmware', 'saves', 'save', 'savedata', 'shader-cache', 'shadercache', 'cache', 'caches', 'sce_sys', 'system_modules', 'fonts', 'app0', 'convert', 'sys', '_sd', 'user', 'shaders', 'logs', 'sce_module', 'eboot.bin'}

def fail():
    raise ValueError('GT7 artifact validation failed')

def resolve_renderer():
    return resolve_commit(RENDERER_API, os.environ.get('RENDERER_OVERRIDE_SHA', '') or 'main')

def resolve():
    global RENDERER
    RENDERER = resolve_renderer()
    commit = resolve_commit(API, os.environ.get('SOURCE_OVERRIDE_SHA', '') or 'main')
    needed = True
    with open(os.environ['GITHUB_OUTPUT'], 'a', encoding='utf-8') as out:
        out.write(f'commit={commit}\nrenderer_commit={RENDERER}\nneeded={str(needed).lower()}\n')
    if os.environ.get('GITHUB_STEP_SUMMARY'):
        with open(os.environ['GITHUB_STEP_SUMMARY'], 'a', encoding='utf-8') as out:
            out.write('### Resolved build sources\n\n'
                      f'- Source: [`{commit}`](https://github.com/RyGT-Team/rygt7/commit/{commit})\n'
                      f'- Renderer: [`{RENDERER}`](https://github.com/RyGT-Team/shadgt7/commit/{RENDERER})\n'
                      f'- Build needed: `{str(needed).lower()}`\n\n')
    print(f'Resolved GT7 main {commit}; shadgt7 main {RENDERER}')

def digest(data):
    return hashlib.sha256(data).hexdigest()

def validate(folder, commit):
    if not SHA.fullmatch(commit) or not SHA.fullmatch(RENDERER): fail()
    folder = Path(folder)
    name = f'RyGT7-ci-{commit[:8]}.zip'
    marker = json.loads((folder / 'CI-BUILD.json').read_text(encoding='utf-8-sig'))
    if (marker.get('schema') != 1 or marker.get('project') != 'rygt-team/rygt7' or
        marker.get('branch') != 'main' or marker.get('commit') != commit or
        marker.get('renderer') != {'configured_repository': CONFIGURED, 'fetched_repository': FETCHED, 'commit': RENDERER} or
        marker.get('runtime_base') != {'url': BASE_URL, 'sha256': BASE} or
        marker.get('build_info') != 'BUILD_INFO.json'): fail()
    archive = folder / name
    with archive.open('rb') as f: checksum = hashlib.file_digest(f, 'sha256').hexdigest()
    if marker.get('archive') != {'name': name, 'sha256': checksum}: fail()
    if (folder / (name + '.sha256')).read_text(encoding='utf-8-sig').strip() != checksum + '  ' + name: fail()
    top = name[:-4]
    with zipfile.ZipFile(archive) as z:
        entries = {}; folded = set(); total = 0
        for info in z.infolist():
            path = info.filename
            parts = path.split('/')
            if (not path or '\\' in path or path.startswith('/') or any(p in ('', '.', '..') for p in parts) or
                any(':' in p or p.endswith(('.', ' ')) for p in parts) or parts[0] != top or len(parts) < 2 or
                any(p.casefold() in BAD for p in parts[1:]) or stat.S_ISLNK(info.external_attr >> 16)):
                fail()
            relative = parts[1:]
            roots = {'libs', 'converter', 'tools', 'patches', 'launcher', 'docs', 'licenses'}
            root_files = {'rygt7.exe', 'build_info.json', 'manifest.json', 'readme.txt', 'license', 'amd_fidelityfx_vk.dll', 'amd_fidelityfx_vk-license.txt', 'libgcc_s_seh-1.dll', 'libstdc++-6.dll', 'libwinpthread-1.dll'}
            if (len(relative) == 1 and relative[0].casefold() not in root_files) or (len(relative) > 1 and relative[0].casefold() not in roots): fail()
            key = path.casefold()
            if key in folded: fail()
            folded.add(key)
            if info.is_dir(): fail()
            total += info.file_size
            if info.file_size > 2 * 1024**3 or total > 8 * 1024**3 or len(folded) > 20000: fail()
            entries['/'.join(parts[1:])] = info
        if not REQUIRED.issubset(entries) or 'manifest.json' not in entries: fail()
        manifest = json.loads(z.read(entries['manifest.json']))
        records = manifest.get('files')
        if not isinstance(records, list): fail()
        seen = set()
        for rec in records:
            path = rec.get('path'); expected = rec.get('sha256')
            if not isinstance(path, str) or path in seen or path not in entries or not isinstance(expected, str) or not DIGEST.fullmatch(expected): fail()
            seen.add(path)
            info = entries[path]
            if rec.get('bytes') != info.file_size or digest(z.read(info)) != expected: fail()
        if seen != set(entries) - {'manifest.json'}: fail()
        source = manifest.get('sources', {}).get('RyGT7', {})
        if source.get('commit') != commit or source.get('uncommitted_changes') is not False: fail()
        renderer_source = manifest.get('sources', {}).get('shadGT7', {})
        if renderer_source.get('commit') != RENDERER or not isinstance(renderer_source.get('uncommitted_changes'), bool): fail()
        build = json.loads(z.read(entries['BUILD_INFO.json']))
        if (build.get('schema') != 1 or build.get('rygt7_commit') != commit or build.get('renderer_commit') != RENDERER or
            build.get('renderer_configured_url') != CONFIGURED or build.get('renderer_fetch_url') != FETCHED or
            build.get('base_url') != BASE_URL or build.get('base_sha256') != BASE): fail()
        control = marker.get('ci_control')
        if control != build.get('ci_control') or not isinstance(control, dict): fail()
        if os.environ.get('GITHUB_ACTIONS') == 'true':
            expected_control = {'repository': os.environ['GITHUB_REPOSITORY'], 'commit': os.environ['GITHUB_SHA'], 'run_url': 'https://github.com/' + os.environ['GITHUB_REPOSITORY'] + '/actions/runs/' + os.environ['GITHUB_RUN_ID'], 'attempt': os.environ['GITHUB_RUN_ATTEMPT']}
            if control != expected_control: fail()
        if build.get('renderer_vma_patch') != {'path': 'externals/vma-patches/0001-trim-empty-default-blocks.patch', 'sha256': '989c12d4729935f11c05a13eeab0fc96fc99e53055c97bd0b81bce04448d6180', 'target': 'include/vk_mem_alloc.h', 'verified': True}: fail()
        runtime = build.get('runtime_files')
        if not isinstance(runtime, dict) or not runtime: fail()
        runtime_paths = {p for p in entries if p.startswith('libs/') or p in {'amd_fidelityfx_vk.dll', 'amd_fidelityfx_vk-LICENSE.txt', 'libgcc_s_seh-1.dll', 'libstdc++-6.dll', 'libwinpthread-1.dll'}}
        if set(runtime) != runtime_paths: fail()
        origins = {'pinned-base', 'rygt7-source-build', 'shadgt7-source-build', 'rygt7-gt7-stub-build', 'ucrt64-toolchain'}
        for path, record in runtime.items():
            if path not in entries or not isinstance(record, dict) or record.get('origin') not in origins or record.get('sha256') != digest(z.read(entries[path])): fail()
        for path in ('libs/libc.prx', 'libs/libkernel.prx', 'libs/libSceFiber.prx', 'libs/libScePad.prx'):
            if runtime.get(path, {}).get('origin') != 'rygt7-source-build': fail()
        if runtime.get('libs/libSceVideoOut.prx', {}).get('origin') != 'shadgt7-source-build': fail()
    print('GT7 artifact validated')

if __name__ == '__main__':
    if len(sys.argv) != 2: fail()
    if sys.argv[1] == 'resolve': resolve()
    elif sys.argv[1] == 'validate': validate(os.environ.get('ARTIFACT_DIR', 'dist'), os.environ['SOURCE_SHA'])
    else: fail()
