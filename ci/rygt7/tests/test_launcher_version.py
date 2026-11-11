import re
import unittest
from pathlib import Path

LOADER = Path(__file__).resolve().parents[3] / 'loader'


class LauncherVersionTests(unittest.TestCase):
    def version(self):
        source = (LOADER / 'Version.cs').read_text(encoding='utf-8-sig')
        match = re.search(r'public const string Value = "([^"]+)";', source)
        self.assertIsNotNone(match, 'LauncherVersion.Value not found in loader/Version.cs')
        return match.group(1)

    def test_version_is_semantic(self):
        self.assertRegex(self.version(), r'^(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)$')

    def test_changelog_documents_current_version_first(self):
        changelog = (LOADER / 'CHANGELOG.md').read_text(encoding='utf-8-sig')
        releases = re.findall(r'^## (\S+)', changelog, re.MULTILINE)
        self.assertTrue(releases, 'loader/CHANGELOG.md has no release sections')
        self.assertEqual(releases[0], self.version(),
                         'Add a loader/CHANGELOG.md section for the version in loader/Version.cs')

    def test_launcher_builds_compile_version_source(self):
        for script in sorted(LOADER.glob('*.cmd')):
            text = script.read_text(encoding='utf-8-sig')
            if 'RyGTLoader.cs' in text:
                self.assertIn('Version.cs', text, f'{script.name} does not compile Version.cs')


if __name__ == '__main__':
    unittest.main()
