"""Release regression: the dealership patch must ship without copying player data."""
import hashlib
import importlib.util
import json
from pathlib import Path
import sys
import tempfile
import unittest
from unittest.mock import patch
import xml.etree.ElementTree as ET
import zipfile

HERE = Path(__file__).resolve().parent
spec = importlib.util.spec_from_file_location("package_release", HERE / "package_release.py")
release = importlib.util.module_from_spec(spec)
spec.loader.exec_module(release)
REPO = HERE.parent


class ReleasePatchTests(unittest.TestCase):
    def test_bundled_patch_and_manifest_exclude_player_data(self):
        with tempfile.TemporaryDirectory(prefix="rygt7-package-") as temp:
            root = Path(temp)
            runtime = root / "runtime"
            # Runtime files are synthetic: no game, firmware or saves are needed.
            names = [line.strip() for line in (HERE / "runtime-files.txt").read_text().splitlines()]
            for name in ["libs/" + n for n in names if n and not n.startswith("#")] + release.ROOT_FILES:
                target = runtime / name
                target.parent.mkdir(parents=True, exist_ok=True)
                target.write_bytes(b"runtime fixture")
            for name in ("patches/CUSA24769.xml", "patches/personal.xml", "_sd/gt7save.bin"):
                target = runtime / name
                target.parent.mkdir(parents=True, exist_ok=True)
                target.write_bytes(b"private player fixture")
            deps = root / "deps"
            (deps / "fixture").mkdir(parents=True)
            (deps / "fixture/LICENSE").write_text("dependency notice")
            gcc = root / "gcc"
            for name in release.GCC_LICENSE_PACKAGES:
                (gcc / name).mkdir(parents=True)
                (gcc / name / "LICENSE").write_text("runtime notice")
            executable = root / "fixture.exe"
            executable.write_bytes(b"executable fixture")
            args = ["package_release.py", "--runtime", str(runtime), "--launcher", str(executable),
                    "--converter", str(executable), "--deps-source", str(deps),
                    "--gcc-licenses", str(gcc), "--out", str(root / "out"),
                    "--name", "release-test", "--zip"]
            with patch.object(sys, "argv", args):
                release.main()
            with zipfile.ZipFile(root / "out/release-test.zip") as archive:
                xml = archive.read("release-test/patches/CUSA24769.xml")
                self.assertEqual(xml, (REPO / "patches/CUSA24769.xml").read_bytes())
                group = ET.fromstring(xml).find("Metadata[@Name='Offline mode']")
                self.assertEqual(len(group.findall("PatchList/Line")), 589)
                for name in ("README.md", "Proper-Offline-Patch-README.txt"):
                    self.assertEqual(archive.read("release-test/patches/" + name),
                                     (REPO / "patches" / name).read_bytes())
                names = archive.namelist()
                self.assertFalse(any("personal.xml" in n or "gt7save.bin" in n for n in names))
                manifest = json.loads(archive.read("release-test/manifest.json"))
                records = {item["path"]: item for item in manifest["files"]}
                self.assertIn("patches/CUSA24769.xml", records)
                # Steering wheels: the launcher's wheel page runs this probe.
                self.assertIn("tools/ryty-wheel-probe.exe", records)
                self.assertIn("release-test/tools/ryty-wheel-probe.exe", names)
                for name in ("CUSA24769.xml", "README.md", "Proper-Offline-Patch-README.txt"):
                    data = archive.read("release-test/patches/" + name)
                    record = records["patches/" + name]
                    self.assertEqual(record["bytes"], len(data))
                    self.assertEqual(record["sha256"], hashlib.sha256(data).hexdigest())


if __name__ == "__main__":
    unittest.main()
