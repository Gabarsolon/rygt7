import io
import json
import unittest
from unittest.mock import patch
import urllib.error
import sys
from pathlib import Path

sys.path.insert(0, str(Path(__file__).resolve().parents[1]))
import github_source as source

SHA = 'a' * 40
API = 'https://api.github.com/repos/RyGT-Team/shadgt7'

class GitHubSourceTests(unittest.TestCase):
    def test_main_and_exact_sha(self):
        for revision in ('main', SHA):
            with patch.object(source.urllib.request, 'urlopen', return_value=io.BytesIO(json.dumps({'sha': SHA}).encode())) as get:
                self.assertEqual(source.resolve_commit(API, revision), SHA)
                self.assertEqual(get.call_args.args[0].full_url, API + '/commits/' + revision)

    def test_invalid_revision_never_fetches(self):
        with patch.object(source.urllib.request, 'urlopen') as get:
            for bad in ('', 'HEAD', '../main', 'A'*40, 'a'*39):
                with self.assertRaises(ValueError): source.resolve_commit(API, bad)
            get.assert_not_called()

    def test_wrong_or_invalid_response_is_rejected(self):
        for actual in ('b'*40, 'invalid'):
            with patch.object(source.urllib.request, 'urlopen', return_value=io.BytesIO(json.dumps({'sha': actual}).encode())):
                with self.assertRaises(ValueError): source.resolve_commit(API, SHA)

    def test_missing_revision_does_not_fall_back(self):
        missing = urllib.error.HTTPError('url', 404, 'Not Found', {}, None)
        with patch.object(source.urllib.request, 'urlopen', side_effect=missing):
            with self.assertRaises(urllib.error.HTTPError): source.resolve_commit(API, SHA)
