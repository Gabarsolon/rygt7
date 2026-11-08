import importlib.util
import io
import json
from pathlib import Path
import unittest
from unittest.mock import patch
import urllib.error

HERE=Path(__file__).resolve().parents[1]
HELPER=HERE / 'github_source.py'
spec=importlib.util.spec_from_file_location('github_source_under_test', HELPER)
source=importlib.util.module_from_spec(spec)
spec.loader.exec_module(source)
SHA='a'*40

class GitHubSourceTests(unittest.TestCase):
    def test_exact_revision_waits_for_relay(self):
        missing=urllib.error.HTTPError('url',404,'Not Found',{},None)
        response=io.BytesIO(json.dumps({'id':SHA}).encode())
        with patch.object(source.urllib.request,'urlopen',side_effect=[missing,response]) as get, patch.object(source.time,'sleep') as sleep:
            self.assertEqual(source.wait_for_commit('https://gitlab.test/api',SHA,attempts=2),SHA)
        self.assertEqual(get.call_count,2)
        self.assertTrue(get.call_args.args[0].endswith('/repository/commits/'+SHA))
        sleep.assert_called_once_with(5)
    def test_bad_revision_never_fetches(self):
        with patch.object(source.urllib.request,'urlopen') as get:
            for bad in ['', 'HEAD', '../main', 'A'*40, 'a'*39]:
                with self.assertRaises(ValueError):source.wait_for_commit('api',bad)
            get.assert_not_called()
    def test_missing_revision_does_not_fall_back(self):
        missing=urllib.error.HTTPError('url',404,'Not Found',{},None)
        with patch.object(source.urllib.request,'urlopen',side_effect=missing), patch.object(source.time,'sleep'):
            with self.assertRaisesRegex(RuntimeError,'did not deliver'):source.wait_for_commit('api',SHA,attempts=1)
    def test_auth_failure_is_not_retried(self):
        denied=urllib.error.HTTPError('url',403,'Forbidden',{},None)
        with patch.object(source.urllib.request,'urlopen',side_effect=denied) as get, patch.object(source.time,'sleep') as sleep:
            with self.assertRaises(urllib.error.HTTPError):source.wait_for_commit('api',SHA)
            self.assertEqual(get.call_count,1)
            sleep.assert_not_called()
    def test_wrong_commit_is_rejected(self):
        response=io.BytesIO(json.dumps({'id':'b'*40}).encode())
        with patch.object(source.urllib.request,'urlopen',return_value=response):
            with self.assertRaises(ValueError):source.wait_for_commit('api',SHA)
