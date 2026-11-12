import importlib.util
import json
import os
from pathlib import Path
import sys
import subprocess
import tempfile
import unittest
from unittest import mock
import io
import urllib.error
import zipfile

MODULE = Path(__file__).resolve().parents[1] / 'registry_ci.py'
spec = importlib.util.spec_from_file_location('registry_ci', MODULE)
m = importlib.util.module_from_spec(spec); spec.loader.exec_module(m)
COMMIT = 'a' * 40
RENDERER = 'b' * 40

AMD = ('amd_fidelityfx_vk.dll', 'amd_fidelityfx_vk-LICENSE.txt')


def origin(path):
    if path in AMD: return 'amd-fidelityfx-sdk'
    return 'shadgt7-source-build' if path.endswith('libSceVideoOut.prx') else 'rygt7-source-build'

class Validation(unittest.TestCase):
    def setUp(self):
        self.tmp = tempfile.TemporaryDirectory(); self.folder = Path(self.tmp.name)
        self.addCleanup(self.tmp.cleanup)
        self.summary = self.folder / 'summary'
        self.output = self.folder / 'workflow-output'
        renderer = mock.patch.object(m, 'RENDERER', RENDERER)
        renderer.start(); self.addCleanup(renderer.stop)
        self.real_resolve_renderer = m.resolve_renderer
        resolver = mock.patch.object(m, 'resolve_renderer', return_value=RENDERER)
        resolver.start(); self.addCleanup(resolver.stop)
        self.env = mock.patch.dict(os.environ, {
            'GITHUB_ACTIONS':'false', 'GITHUB_STEP_SUMMARY':str(self.summary),
            'GITHUB_OUTPUT':str(self.output), 'SOURCE_OVERRIDE_SHA':'',
            'RENDERER_OVERRIDE_SHA':'', 'FORCE_BUILD':'false',
        })
        self.env.start(); self.addCleanup(self.env.stop)
        self.files = {name: b'fixture' for name in m.REQUIRED if name != 'BUILD_INFO.json'}
        for n in ('libc.prx','libkernel.prx','libScePad.prx'): self.files['libs/'+n]=b'fixture'
        for n in AMD: self.files[n]=b'fixture'
        self.info = {'schema': 1, 'rygt7_commit': COMMIT, 'renderer_commit': m.RENDERER,
                     'renderer_configured_url': m.CONFIGURED, 'renderer_fetch_url': m.FETCHED,
                     'renderer_vma_patch': {'path':'externals/vma-patches/0001-trim-empty-default-blocks.patch','sha256':'989c12d4729935f11c05a13eeab0fc96fc99e53055c97bd0b81bce04448d6180','target':'include/vk_mem_alloc.h','verified':True}, 'ci_control': {'repository':None,'commit':None,'run_url':None,'attempt':None},
                     'runtime_files': {p: {'origin': origin(p), 'sha256': m.digest(b'fixture')} for p in self.files if p.startswith('libs/') or p in AMD}}
    def write(self, extra=(), info=None, bad_hash=False):
        files = dict(self.files); files['BUILD_INFO.json'] = json.dumps(info or self.info).encode()
        records = [{'path': p, 'bytes': len(b), 'sha256': m.digest(b)} for p,b in files.items()]
        if bad_hash: records[0]['sha256'] = '0'*64
        manifest = {'sources': {'RyGT7': {'commit': COMMIT, 'uncommitted_changes': False}, 'shadGT7': {'commit':m.RENDERER, 'uncommitted_changes':False}}, 'files': records}
        name = f'RyGT7-ci-{COMMIT[:8]}.zip'; top = name[:-4]
        with zipfile.ZipFile(self.folder / name, 'w') as z:
            for p,b in files.items(): z.writestr(top+'/'+p,b)
            z.writestr(top+'/manifest.json', json.dumps(manifest))
            for p in extra: z.writestr(top+'/'+p,b'x')
        checksum = m.digest((self.folder/name).read_bytes())
        (self.folder/(name+'.sha256')).write_text(checksum+'  '+name+'\n')
        marker = {'schema':1, 'project':'rygt-team/rygt7', 'branch':'main', 'commit':COMMIT,
                  'renderer': {'configured_repository':m.CONFIGURED, 'fetched_repository':m.FETCHED, 'commit':m.RENDERER},
                  'ci_control': {'repository':None,'commit':None,'run_url':None,'attempt':None}, 'archive':{'name':name,'sha256':checksum}, 'build_info':'BUILD_INFO.json'}
        (self.folder/'CI-BUILD.json').write_text(json.dumps(marker))
    def test_valid(self):
        self.write(); m.validate(self.folder, COMMIT)
    def test_missing_components(self):
        for path in ('RyGT7.exe','converter/ryty.exe','libs/libSceVideoOut.prx'):
            value=self.files.pop(path); self.write()
            with self.assertRaises(ValueError): m.validate(self.folder, COMMIT)
            self.files[path]=value
    def test_unsafe_and_duplicate(self):
        for path in ('../escape', 'GAME/eboot.bin', 'rygt7.EXE', 'docs/app0/file', 'libs/sce_module/file', 'user/file'):
            self.write(extra=[path])
            with self.assertRaises(ValueError): m.validate(self.folder, COMMIT)
    def test_provenance_and_hash(self):
        for field in ('rygt7_commit','renderer_commit'):
            info=dict(self.info); info[field]='0'*40; self.write(info=info)
            with self.assertRaises(ValueError): m.validate(self.folder, COMMIT)
        # FEAT-GTS-253: a package built over a runtime archive is rejected.
        info=dict(self.info, base_url='https://example.invalid/base.zip', base_sha256='0'*64); self.write(info=info)
        with self.assertRaises(ValueError): m.validate(self.folder, COMMIT)
        self.write(bad_hash=True)
        with self.assertRaises(ValueError): m.validate(self.folder, COMMIT)
    def test_hosted_metadata_enforced(self):
        self.write()
        hosted = {'GITHUB_ACTIONS':'true', 'GITHUB_REPOSITORY':'RyGT-Team/RyGT-ci',
                  'GITHUB_SHA':'b'*40, 'GITHUB_RUN_ID':'123', 'GITHUB_RUN_ATTEMPT':'1'}
        with mock.patch.dict(os.environ, hosted):
            with self.assertRaises(ValueError): m.validate(self.folder, COMMIT)
        control = {'repository':hosted['GITHUB_REPOSITORY'], 'commit':hosted['GITHUB_SHA'],
                   'run_url':'https://github.com/RyGT-Team/RyGT-ci/actions/runs/123', 'attempt':'1'}
        self.info['ci_control'] = control
        self.write()
        marker = json.loads((self.folder/'CI-BUILD.json').read_text())
        marker['ci_control'] = control
        (self.folder/'CI-BUILD.json').write_text(json.dumps(marker))
        with mock.patch.dict(os.environ, hosted): m.validate(self.folder, COMMIT)
    def test_mismatched_source_and_checksum(self):
        self.write()
        with self.assertRaises(ValueError): m.validate(self.folder,'b'*40)
        checksum=next(self.folder.glob('*.sha256')); checksum.write_text('invalid')
        with self.assertRaises(ValueError): m.validate(self.folder,COMMIT)

    def test_runtime_coverage_and_origin(self):
        for mode in ('missing', 'wrong_origin', 'amd_origin', 'extra'):
            info=dict(self.info); info['runtime_files']={k:dict(v) for k,v in self.info['runtime_files'].items()}
            if mode == 'missing': info['runtime_files'].pop('libs/libSceFiber.prx')
            elif mode == 'wrong_origin': info['runtime_files']['libs/libSceFiber.prx']['origin']='pinned-base'
            elif mode == 'amd_origin': info['runtime_files']['amd_fidelityfx_vk.dll']['origin']='pinned-base'
            else: info['runtime_files']['not-present']={'origin':'pinned-base','sha256':'0'*64}
            self.write(info=info)
            with self.assertRaises(ValueError): m.validate(self.folder,COMMIT)
    def test_no_secret_in_output(self):
        self.write()
        output=io.StringIO()
        with mock.patch('sys.stdout',output): m.validate(self.folder,COMMIT)
        self.assertNotIn('secret-value',output.getvalue())
    def test_resolve_always_builds_without_registry_queries(self):
        with mock.patch.object(m.urllib.request, 'urlopen', return_value=io.BytesIO(json.dumps({'sha': COMMIT}).encode())) as request:
            m.resolve()
        self.assertEqual(request.call_count, 1)
        self.assertIn('needed=true', self.output.read_text())
        self.assertIn('renderer_commit='+RENDERER, self.output.read_text())
        summary = self.summary.read_text()
        self.assertIn(f'https://github.com/RyGT-Team/rygt7/commit/{COMMIT}', summary)
        self.assertIn(f'https://github.com/RyGT-Team/shadgt7/commit/{RENDERER}', summary)

    def test_renderer_head_resolution(self):
        class Response(io.BytesIO): pass
        with mock.patch.object(m.urllib.request, 'urlopen', return_value=Response(json.dumps({'sha':RENDERER}).encode())) as request:
            self.assertEqual(self.real_resolve_renderer(), RENDERER)
            self.assertEqual(request.call_args.args[0].full_url, 'https://api.github.com/repos/RyGT-Team/shadgt7/commits/main')
        for invalid in ('', 'z'*40, 'a'*39):
            with mock.patch.object(m.urllib.request, 'urlopen', return_value=Response(json.dumps({'sha':invalid}).encode())):
                with self.assertRaises(ValueError): self.real_resolve_renderer()

    def test_invalid_renderer_rejected(self):
        self.write()
        for invalid in ('', 'z'*40, 'a'*39):
            with mock.patch.object(m, 'RENDERER', invalid):
                with self.assertRaises(ValueError): m.validate(self.folder, COMMIT)

class WorkflowEnvironmentIsolation(unittest.TestCase):
    def test_fixtures_do_not_write_hosted_workflow_files(self):
        with tempfile.TemporaryDirectory() as directory:
            root = Path(directory)
            summary, output = root / 'host-summary', root / 'host-output'
            summary.write_text('real hosted summary\n')
            output.write_text('real_output=unchanged\n')
            environment = dict(os.environ, GITHUB_ACTIONS='true',
                               GITHUB_STEP_SUMMARY=str(summary), GITHUB_OUTPUT=str(output))
            completed = subprocess.run([sys.executable, str(Path(__file__).resolve()), 'Validation'],
                                       env=environment, capture_output=True, text=True)
            self.assertEqual(completed.returncode, 0, completed.stdout + completed.stderr)
            self.assertEqual(summary.read_text(), 'real hosted summary\n')
            self.assertEqual(output.read_text(), 'real_output=unchanged\n')


if __name__ == '__main__': unittest.main()
