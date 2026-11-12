"""The pinned MSYS2 UCRT64 GCC toolchain that builds the runtime libraries (libs/*.prx).

The runtime libraries need MinGW-w64 GCC with MSYS2's UCRT64 packages: the winlibs build documented
earlier no longer compiles the current source. CI and local builds unpack the exact package set listed
in ci/rygt7/runtime-toolchain.json (file names and SHA-256s), the same pins as RyGTS's ci/runtime-toolchain.json, so every build uses the same compiler.

  python ci/rygt7/runtime_toolchain.py install <folder>   download, verify and unpack the pinned packages;
                                                      prints the folder holding gcc.exe
  python ci/rygt7/runtime_toolchain.py ffmpeg <folder>    download, verify and unpack the pinned FFmpeg
                                                      package; prints the folder for FFMPEG_PREBUILT_DIR
  python ci/rygt7/runtime_toolchain.py fidelityfx <folder>  download and verify AMD's FidelityFX runtime
                                                      (amd_fidelityfx_vk.dll and its license); prints the folder
  python ci/rygt7/runtime_toolchain.py pin <cutoff date>  (maintainers) rewrite the pins: each package at the
                                                      newest version MSYS2 published on or before the date
"""
import hashlib
import json
import re
import sys
import tarfile
import urllib.parse
import urllib.request
from datetime import datetime
from pathlib import Path, PurePosixPath

REPOSITORY = 'https://repo.msys2.org/mingw/ucrt64/'
PINS = Path(__file__).with_name('runtime-toolchain.json')
# GCC 16.2.0-6 and what it needs to compile and link (from the packages' .PKGINFO depend lines).
EXACT = {'gcc': '16.2.0-6', 'libgcc': '16.2.0-6', 'libstdc++': '16.2.0-6', 'gcc-libs': '16.2.0-6',
         'libgomp': '16.2.0-6', 'libatomic': '16.2.0-6', 'libquadmath': '16.2.0-6'}
NEWEST = ('binutils', 'crt', 'headers', 'winpthreads', 'libwinpthread', 'gmp', 'mpfr', 'mpc', 'isl',
          'zlib', 'zstd', 'libiconv', 'gettext-runtime', 'windows-default-manifest', 'tzdata')
PREFIX = 'mingw-w64-ucrt-x86_64-'


def sha256(path):
    digest = hashlib.sha256()
    with open(path, 'rb') as stream:
        for block in iter(lambda: stream.read(1 << 20), b''):
            digest.update(block)
    return digest.hexdigest()


def download(name, target):
    if not target.exists():
        part = target.with_suffix(target.suffix + '.part')
        with urllib.request.urlopen(REPOSITORY + urllib.parse.quote(name)) as response, open(part, 'wb') as output:
            while True:
                block = response.read(1 << 20)
                if not block:
                    break
                output.write(block)
        part.replace(target)
    return target


def unpack(package, folder):
    """Unpacks a .pkg.tar.zst into `folder`, skipping pacman's metadata and anything unsafe."""
    import zstandard  # pinned in CI: pip install zstandard==0.25.0
    with open(package, 'rb') as raw, zstandard.ZstdDecompressor().stream_reader(raw) as stream:
        with tarfile.open(fileobj=stream, mode='r|') as archive:
            for member in archive:
                path = PurePosixPath(member.name)
                if member.name.startswith('.') or path.is_absolute() or '..' in path.parts:
                    continue
                target = Path(folder, *path.parts)
                if member.islnk():
                    # Hard links (g++.exe is one of c++.exe): a copy of the file already unpacked.
                    linked = PurePosixPath(member.linkname)
                    if linked.is_absolute() or '..' in linked.parts:
                        continue
                    target.parent.mkdir(parents=True, exist_ok=True)
                    target.write_bytes(Path(folder, *linked.parts).read_bytes())
                    continue
                if not (member.isfile() or member.isdir()):
                    continue
                if member.isdir():
                    target.mkdir(parents=True, exist_ok=True)
                    continue
                target.parent.mkdir(parents=True, exist_ok=True)
                with archive.extractfile(member) as source, open(target, 'wb') as output:
                    while True:
                        block = source.read(1 << 20)
                        if not block:
                            break
                        output.write(block)


def install(folder):
    folder = Path(folder)
    cache = folder / 'packages'
    root = folder / 'msys2-ucrt'
    cache.mkdir(parents=True, exist_ok=True)
    pins = json.loads(PINS.read_text(encoding='utf-8-sig'))
    marker = root / 'PINS.json'
    if marker.exists() and json.loads(marker.read_text(encoding='utf-8-sig')) == pins:
        return root / 'ucrt64' / 'bin'
    for package in pins['packages']:
        path = download(package['file'], cache / package['file'])
        if sha256(path) != package['sha256']:
            path.unlink()
            raise RuntimeError('SHA-256 mismatch for ' + package['file'])
    for package in pins['packages']:
        unpack(cache / package['file'], root)
    bin_folder = root / 'ucrt64' / 'bin'
    if not (bin_folder / 'g++.exe').exists():
        raise RuntimeError('g++.exe missing after unpacking the toolchain')
    marker.write_text(json.dumps(pins, indent=2) + '\n', encoding='utf-8')
    return bin_folder


def ffmpeg(folder):
    """Downloads and unpacks 3rdparty/ffmpeg-core's prebuilt package for MinGW, checked by SHA-256.
    Returns the folder for -DFFMPEG_PREBUILT_DIR (the one holding include/libavcodec)."""
    import zipfile
    pinned = json.loads(PINS.read_text(encoding='utf-8-sig'))['ffmpeg']
    folder = Path(folder)
    root = folder / ('ffmpeg-' + pinned['commit'])
    if (root / 'include/libavcodec/avcodec.h').exists():
        return root
    folder.mkdir(parents=True, exist_ok=True)
    archive = folder / pinned['file']
    if not archive.exists():
        with urllib.request.urlopen(pinned['url']) as response, open(archive, 'wb') as output:
            output.write(response.read())
    if sha256(archive) != pinned['sha256']:
        archive.unlink()
        raise RuntimeError('SHA-256 mismatch for ' + pinned['file'])
    with zipfile.ZipFile(archive) as package:
        for entry in package.infolist():
            path = PurePosixPath(entry.filename)
            if path.is_absolute() or '..' in path.parts:
                raise RuntimeError('Unsafe path in ' + pinned['file'])
        package.extractall(root)
    if not (root / 'include/libavcodec/avcodec.h').exists():
        raise RuntimeError('FFmpeg package has no include/libavcodec/avcodec.h')
    return root


def fidelityfx(folder):
    """FEAT-GTS-253: downloads AMD's signed FidelityFX runtime from its SDK release, checked by SHA-256."""
    pinned = json.loads(PINS.read_text(encoding='utf-8-sig'))['fidelityfx']
    folder = Path(folder) / 'fidelityfx'
    folder.mkdir(parents=True, exist_ok=True)
    for url, digest, name in ((pinned['dll_url'], pinned['dll_sha256'], 'amd_fidelityfx_vk.dll'),
                              (pinned['license_url'], pinned['license_sha256'], 'amd_fidelityfx_vk-LICENSE.txt')):
        target = folder / name
        if not target.exists() or sha256(target) != digest:
            with urllib.request.urlopen(url) as response, open(target, 'wb') as output:
                output.write(response.read())
        if sha256(target) != digest:
            target.unlink()
            raise RuntimeError('SHA-256 mismatch for ' + name)
    return folder


def pin(cutoff):
    """Rewrites runtime-toolchain.json from MSYS2's file list (maintainers only)."""
    limit = datetime.strptime(cutoff, '%Y-%m-%d')
    with urllib.request.urlopen(REPOSITORY) as response:
        index = response.read().decode('utf-8', 'replace')
    listed = {}
    for name, stamp in re.findall(r'href="(' + re.escape(PREFIX) + r'[^"]+\.pkg\.tar\.zst)">[^<]*</a>\s*(\d{2}-\w{3}-\d{4} \d{2}:\d{2})', index):
        listed[urllib.parse.unquote(name)] = datetime.strptime(stamp, '%d-%b-%Y %H:%M')  # libstdc%2B%2B
    chosen = []
    for short in list(EXACT) + list(NEWEST):
        pattern = re.compile(re.escape(PREFIX + short) + r'-(\d[^-]*-\d+)-any\.pkg\.tar\.zst$')
        candidates = [(listed[name], name) for name in listed
                      if pattern.match(name) and (short not in EXACT or pattern.match(name).group(1) == EXACT[short])]
        candidates = [item for item in candidates if item[0] <= limit.replace(hour=23, minute=59)]
        if not candidates:
            if short in EXACT:
                raise RuntimeError('MSYS2 has no ' + short + ' ' + EXACT[short])
            continue
        published, name = max(candidates)
        chosen.append((name, published))
    packages = []
    cache = Path('.runtime-toolchain-cache')
    cache.mkdir(exist_ok=True)
    for name, published in chosen:
        packages.append({'file': name, 'published': published.strftime('%Y-%m-%d %H:%M'),
                         'sha256': sha256(download(name, cache / name))})
    previous = json.loads(PINS.read_text(encoding='utf-8-sig')) if PINS.exists() else {}
    document = {'repository': REPOSITORY, 'cutoff': cutoff, 'packages': packages}
    for key, value in previous.items():  # ffmpeg, fidelityfx: pinned by hand
        document.setdefault(key, value)
    PINS.write_text(json.dumps(document, indent=2) + '\n', encoding='utf-8')
    for package in packages:
        print(package['file'], package['published'])


if __name__ == '__main__':
    if len(sys.argv) == 3 and sys.argv[1] == 'install':
        print(install(sys.argv[2]))
    elif len(sys.argv) == 3 and sys.argv[1] == 'ffmpeg':
        print(ffmpeg(sys.argv[2]))
    elif len(sys.argv) == 3 and sys.argv[1] == 'fidelityfx':
        print(fidelityfx(sys.argv[2]))
    elif len(sys.argv) == 3 and sys.argv[1] == 'pin':
        pin(sys.argv[2])
    else:
        sys.exit(__doc__)
