# Build and release

## Runtime and converter

Clone RyGT7 with submodules (`git submodule update --init --recursive`). In an MSYS2 UCRT64
environment with GCC, CMake, Ninja and the project's dependencies:

```
cmake -S . -B build -G Ninja -DCMAKE_BUILD_TYPE=Release -DRTY_BUILD_GUI=OFF -DBUILD_TESTING=OFF -DRTY_ENABLE_LTO=OFF
cmake --build build --target ryty --parallel 8
cmake --build build --parallel 8
loader\build.cmd <launcher-output>
loader\test-gt7.cmd <temporary-test-output>
```

Native libraries are emitted under `build/core/libs/libs`; the converter is
`build/core/relinker/ryty.exe`. Runtime DLL dependencies must come from the same UCRT64 toolchain.
The release manifest identifies the shipped files by SHA-256.

## Renderer

Clone the repository and commit in `renderer.json`, with its submodules. From a Visual Studio x64
developer environment with clang-cl, Python, CMake, Ninja and the Windows SDK:

```
cmake --preset x64-Clang-Release -DSHADGT_RYTY_GPU=ON -DHAVE_ENDIAN_H=OFF
cmake --build Build/x64-Clang-Release --target ryty_gpu ryty_audio3d ryty_ajm ryty_ngs2 ryty_zlib ryty_discmap ryty_screenshot ryty_np --parallel 8
```

The renderer CMake configuration applies its checked-in VMA patches. Copy its `.prx` libraries over
the corresponding native runtime libraries, and build the supplemental stubs in `runtime/gt7`.
The first prerelease was built with MSYS2 UCRT64 GCC for the runtime and LLVM/Visual Studio for
the renderer, using the established upstream release's remaining runtime libraries. It is a mixed
toolchain distribution; the manifest records source revisions and provenance. A fresh complete build
is not promised to be byte-identical to the release.

## Packaging

`package_release.py` copies only the file names in `runtime-files.txt`, four named redistributable
DLL/license files, the converter and its three GCC runtime DLLs, launcher, documentation and licenses.
Its input must be an assembled open-source runtime, not an extracted game folder.
No `app0`, `convert`, `sys`, `_sd`, `user`, `shaders`, `logs`, `patches`, or personal settings are copied.
The generated manifest records SHA-256 hashes. The archive is experimental Windows x64 software.

Pass `--help` for paths. Source archives for both tagged repositories accompany the binary release;
dependency checkout revisions are recorded in each repository's `.gitmodules` and Git links.
Source dependency licenses are copied under `licenses` with their relative paths.
