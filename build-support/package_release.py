"""Packages a ryGT7 release folder (and optionally a zip) from an assembled runtime folder.

Usage:
  python -I package_release.py --runtime <folder> --launcher <RyGT7.exe> --converter <ryty.exe>
      --out <folder> [--renderer-source <shadGT7 checkout>] [--name RyGT7-<date>-prerelease] [--zip]
      [--server <RyGTServer.exe> [--shadnet <shadNet folder, default server/shadnet>]]

Only these go in:
  - libs\\: the file names listed in runtime-files.txt, from <runtime>\\libs
  - amd_fidelityfx_vk.dll and its license, and the three GCC runtime DLLs, from <runtime>
  - converter\\: ryty.exe and the GCC runtime DLLs
  - RyGT7.exe and launcher\\ (original background artwork and its note)
  - patches\\CUSA24769.xml and its provenance/credits from this repository
  - README.txt, LICENSE, licenses\\ and manifest.json (SHA-256 of every file, source commits)
  - with --server and --shadnet, server\\ for hosting online play: RyGTServer.exe (server/build.cmd),
    libzstd.dll, this repository's server/data (RyGTServer's own replies) as server-data, and
    shadNet's program files from the given folder. shadNet's data (accounts, logs) is left out and
    its shadnet.cfg goes in with its keys blank: the launcher makes them on the host's PC.
    Polyphony's own files (client settings, texts, event lists) are not packaged: RyGTServer
    fetches them from Polyphony's public servers when GT7 first asks.
Nothing else of the runtime folder is read: no app0, convert, sys, _sd, user, shaders, logs,
player patches, saves or settings. A missing expected file stops the packaging.
"""
import argparse
import datetime
import hashlib
import json
import os
import shutil
import subprocess
import zipfile
from pathlib import Path

HERE = Path(__file__).resolve().parent
REPO = HERE.parent
ROOT_FILES = ["amd_fidelityfx_vk.dll", "amd_fidelityfx_vk-LICENSE.txt", "libgcc_s_seh-1.dll",
              "libstdc++-6.dll", "libwinpthread-1.dll"]
GCC_DLLS = ROOT_FILES[2:]
GCC_LICENSE_PACKAGES = ["libgcc", "libstdc++", "libwinpthread"]
LICENSE_NAMES = ("LICENSE", "LICENCE", "COPYING")
PROVENANCE = (
    "libSceVideoOut.prx (shadGT7 renderer) and libSceAudio3d.prx are built from the shadGT7 "
    "commit in sources; libc.prx, libkernel.prx, libSceFiber.prx and libScePad.prx and the "
    "converter from the RyGT7 commit (MSYS2 UCRT64 GCC 16.2, from 2026-10-27); "
    "libSceTextToSpeech.prx and libSceMbus.prx from runtime/gt7; "
    "libSceAppContentUtil.prx, libSceVideoRecording.prx and libMozaSDK-PS4.prx are copies of "
    "Ryty's empty placeholder module. The other libraries are ryGT's GT Sport build of "
    "2026-10-22 (RyGT commit 882312d, in this repository's history).")


# shadNet: program files only. Its data (accounts database, logs, scores, trophies) and backups stay
# behind; shadnet.cfg is rewritten with its keys blank.
SHADNET_SKIP_DIRS = {"db", "score_data", "tss_data", "logs"}
SHADNET_SKIP_SUFFIXES = (".log", ".running", ".db", ".db-shm", ".db-wal")
SHADNET_KEYS = ("MemberApiKey", "AdminApiKey", "RegistrationSecretKey", "AdminsList")


def put_server(package, args):
    """server\\ for hosting online play (see the top of this file)."""
    if not (args.server and args.shadnet):
        raise SystemExit("--server needs a shadNet folder (--shadnet)")
    package.put(args.server, "server\\RyGTServer.exe")
    package.put(args.runtime_zstd, "server\\libzstd.dll")
    package.put_tree(args.gcc_licenses / "zstd", "licenses\\zstd")
    package.put(REPO / "server" / "README.md", "docs\\server.md")
    package.put_tree(REPO / "server" / "data", "server\\server-data")
    for path in sorted(args.shadnet.rglob("*")):
        relative = path.relative_to(args.shadnet)
        if (not path.is_file() or relative.parts[0] in SHADNET_SKIP_DIRS or path.name.endswith(SHADNET_SKIP_SUFFIXES)
                or ".before" in path.name or path.name == "vc_redist.x64.exe"):
            continue
        if path.name == "shadnet.cfg":
            lines = path.read_text(encoding="utf-8").splitlines()
            lines = [l.split("=", 1)[0] + "=" if l.split("=", 1)[0].strip() in SHADNET_KEYS else l for l in lines]
            package.put_text("\n".join(lines) + "\n", "server\\shadnet\\shadnet.cfg")
            continue
        package.put(path, "server\\shadnet\\" + str(relative))


def is_license(name):
    upper = name.upper()
    return any(upper == n or upper.startswith((n + ".", n + "-", n + "_")) for n in LICENSE_NAMES)


def git(repo, *args):
    try:
        return subprocess.check_output(["git", "-C", str(repo)] + list(args), text=True,
                                       stderr=subprocess.DEVNULL).strip()
    except (OSError, subprocess.CalledProcessError):
        return ""


def revision(repo):
    commit = git(repo, "rev-parse", "HEAD")
    if not commit:
        return None
    return {"commit": commit, "branch": git(repo, "rev-parse", "--abbrev-ref", "HEAD"),
            "uncommitted_changes": bool(git(repo, "status", "--porcelain", "--untracked-files=no"))}


class Package:
    def __init__(self, folder):
        self.folder = folder
        self.files = []

    def _record(self, target, relative):
        self.files.append({"path": relative.replace("\\", "/"), "bytes": target.stat().st_size,
                           "sha256": hashlib.sha256(target.read_bytes()).hexdigest()})

    def put(self, source, relative):
        source = Path(source)
        if not source.is_file():
            raise SystemExit("missing: %s (for %s)" % (source, relative))
        target = self.folder / relative
        target.parent.mkdir(parents=True, exist_ok=True)
        shutil.copy2(source, target)
        self._record(target, relative)

    def put_text(self, text, relative):
        target = self.folder / relative
        target.parent.mkdir(parents=True, exist_ok=True)
        target.write_text(text, encoding="utf-8", newline="\r\n")
        self._record(target, relative)

    def put_tree(self, source, relative):
        for root, _, names in os.walk(source):
            for name in sorted(names):
                path = Path(root) / name
                self.put(path, str(Path(relative) / path.relative_to(source)))

    def put_dependency_licenses(self, folder, relative, what):
        """License files at the first level of every dependency checkout under folder."""
        if not folder.is_dir():
            raise SystemExit("%s: %s is not a folder" % (what, folder))
        found = 0
        for dependency in sorted(p for p in folder.iterdir() if p.is_dir()):
            for entry in sorted(dependency.iterdir()):
                if entry.is_file() and is_license(entry.name):
                    self.put(entry, str(Path(relative) / dependency.name / entry.name))
                    found += 1
                elif entry.is_dir() and entry.name.upper() == "LICENSES":
                    self.put_tree(entry, str(Path(relative) / dependency.name / entry.name))
                    found += 1
        if found == 0:
            raise SystemExit("%s: no license files under %s (submodules missing?)" % (what, folder))
        return found


def main():
    parser = argparse.ArgumentParser(description=__doc__,
                                     formatter_class=argparse.RawDescriptionHelpFormatter)
    parser.add_argument("--runtime", required=True, type=Path)
    parser.add_argument("--launcher", required=True, type=Path)
    parser.add_argument("--converter", required=True, type=Path)
    parser.add_argument("--out", required=True, type=Path)
    parser.add_argument("--renderer-source", type=Path)
    parser.add_argument("--renderer-deps-source", type=Path,
                        help="externals folder with checked-out submodules (default: the renderer's)")
    parser.add_argument("--deps-source", type=Path, default=REPO / "3rdparty",
                        help="checked-out 3rdparty folder of this repository")
    parser.add_argument("--gcc-licenses", type=Path,
                        default=Path(r"C:\msys64\ucrt64\share\licenses"))
    parser.add_argument("--server", type=Path, help="RyGTServer.exe built with server/build.cmd")
    parser.add_argument("--shadnet", type=Path, default=REPO / "server" / "shadnet",
                        help="a shadNet server folder (shadnet.exe and its DLLs); default this repository's")
    parser.add_argument("--runtime-zstd", type=Path, default=Path(r"C:\msys64\ucrt64\bin\libzstd.dll"),
                        help="the libzstd.dll RyGTServer loads (MSYS2 UCRT64's)")
    parser.add_argument("--name")
    parser.add_argument("--zip", action="store_true")
    args = parser.parse_args()

    today = datetime.date.today().isoformat()
    name = args.name or "RyGT7-%s-prerelease" % today
    folder = args.out / name
    if folder.exists():
        shutil.rmtree(folder)
    folder.mkdir(parents=True)
    package = Package(folder)

    names = [l.strip() for l in (HERE / "runtime-files.txt").read_text().splitlines()]
    for n in (n for n in names if n and not n.startswith("#")):
        package.put(args.runtime / "libs" / n, "libs\\" + n)
    for n in ROOT_FILES:
        package.put(args.runtime / n, n)
    package.put(args.converter, "converter\\ryty.exe")
    for n in GCC_DLLS:
        package.put(args.runtime / n, "converter\\" + n)
    package.put(args.launcher, "RyGT7.exe")
    for n in ("CUSA24769.xml", "README.md", "Proper-Offline-Patch-README.txt"):
        package.put(REPO / "patches" / n, "patches\\" + n)
    package.put(REPO / "loader" / "launcher" / "background.png", "launcher\\background.png")
    package.put(REPO / "loader" / "launcher" / "logo.png", "launcher\\logo.png")
    package.put(REPO / "loader" / "launcher" / "README.txt", "launcher\\README.txt")
    package.put(REPO / "LICENSE", "LICENSE")
    package.put(REPO / "LICENSE", "licenses\\RyGT7\\LICENSE")
    package.put(REPO / "AUTHORS.md", "licenses\\RyGT7\\AUTHORS.md")
    package.put(REPO / "loader" / "README.md", "docs\\launcher.md")
    package.put(REPO / "runtime" / "gt7" / "README.md", "docs\\runtime-gt7.md")
    counts = {"RyGT7 deps": package.put_dependency_licenses(
        args.deps_source, "licenses\\RyGT7\\3rdparty", "RyGT7 dependencies")}
    sources = {"RyGT7": revision(REPO)}
    if args.renderer_source:
        package.put(args.renderer_source / "LICENSE", "licenses\\shadGT7\\LICENSE")
        counts["shadGT7 deps"] = package.put_dependency_licenses(
            args.renderer_deps_source or args.renderer_source / "externals", "licenses\\shadGT7\\externals",
            "shadGT7 dependencies")
        sources["shadGT7"] = revision(args.renderer_source)
    for n in GCC_LICENSE_PACKAGES:
        source = args.gcc_licenses / n
        if not source.is_dir():
            raise SystemExit("GCC runtime license notices not found: %s" % source)
        package.put_tree(source, "licenses\\gcc-runtime\\" + n)
    if args.server:
        put_server(package, args)
    readme = (HERE / "release-readme.txt").read_text(encoding="utf-8")
    package.put_text(readme.replace("{name}", name).replace("{date}", today), "README.txt")

    manifest = {"name": name, "built": datetime.datetime.now().astimezone().isoformat(),
                "sources": sources, "runtime_provenance": PROVENANCE,
                "files": sorted(package.files, key=lambda f: f["path"])}
    (folder / "manifest.json").write_text(json.dumps(manifest, indent=1), encoding="utf-8")
    total = sum(f["bytes"] for f in package.files)
    print("%s: %d files, %.1f MB; license notices: %s" % (folder, len(package.files) + 1,
          total / 1e6, ", ".join("%s %d" % kv for kv in counts.items())))
    if args.zip:
        archive = args.out / (name + ".zip")
        with zipfile.ZipFile(archive, "w", zipfile.ZIP_DEFLATED, compresslevel=6) as z:
            for root, _, files in os.walk(folder):
                for n in sorted(files):
                    path = Path(root) / n
                    z.write(path, str(Path(name) / path.relative_to(folder)))
        print("%s: %.1f MB" % (archive, archive.stat().st_size / 1e6))


if __name__ == "__main__":
    main()
