# ryGT7

An experimental Windows porting runtime for **Gran Turismo 7 (PS4, CUSA24769)**, derived from
[ryGT](https://github.com/mlgprorektm8/RyGT) and [Ryty](https://github.com/aarvsn/Ryty).
Rendering uses [shadGT7](https://github.com/mlgprorektm8/shadGT7), derived from shadGT/shadPS4.
The converter prepares the player's executable locally; game code runs on the host CPU and graphics
are translated to Vulkan.

## Download and set up

Download the Windows archive from [Releases](https://github.com/mlgprorektm8/RyGT7/releases).
Extract the whole archive to a writable folder and run **RyGT7.exe**.

You provide:

- Your decrypted **CUSA24769 base game**, with `eboot.bin`, `sce_sys`, `sce_module`, and `contents`.
- An extracted update, either already merged into the base game or in a separate folder.
- Your PS4 firmware's system modules and the fonts folder containing `font` and `font2`.

Select those folders, press **Set up**, wait for conversion, then press **Play**. Leave Update empty
when your game folder is already merged. Separate updates override matching files individually,
preserving base files the update does not replace. Keep the source folders after setup because
game data is linked rather than copied. Use the same drive, or Windows Developer Mode for cross-drive links.

The archive contains no game dump, converted game executable, firmware, fonts, saves, or shader cache.
Python is not required for player setup. Windows x64, a Vulkan-capable GPU, and current graphics
drivers are required. Read [the launcher guide](loader/README.md) for the exact folder layout.

## Alpha status

Verified locally: clean-folder conversion and launch to the GT7 main menu, and racing at Deep Forest.
The tested dump reports `APP_VER=01.00` in its metadata. Other updates and hardware are unverified;
folder-selection support does not guarantee that every update converts or plays correctly.
Crashes, rendering errors, and shader compilation stutter remain possible. Online functionality is
not established by these tests.

This first prerelease fixes crowd instances flickering black or jumping because GPU output ranges
overlapped, and black streamed textures on Deep Forest's pit building. Validation included 512
shader-lowering checks, GPU captures with no remaining overlapping crowd writes, and 60-frame
crowd recordings without the large black flashes.

The launcher retains display, FSR, optional DLSS, input bindings, controller ports, wheel, shader
compilation, and logging controls. GT Sport-specific script preparation and graphics patches are
removed from its interface. DLSS's separate plugin and NVIDIA files are not bundled; see
[the optional plugin instructions](plugins/dlss/README.md). AMD FSR is available in the supplied renderer.

## Source and builds

- [Launcher and setup tests](loader): build with Windows' .NET Framework C# compiler.
- [Runtime libraries and converter](core): build with CMake, Ninja, and MSYS2 UCRT64 GCC.
- [GT7 compatibility stubs](runtime/gt7): source and definitions for the two supplemental libraries.
- [Renderer](https://github.com/mlgprorektm8/shadGT7): separate repository, pinned in
  [build-support/renderer.json](build-support/renderer.json).
- [Build and packaging instructions](build-support/README.md).

## Credits and license

This repository preserves ryGT/Ryty's Git history and license notices. See [AUTHORS.md](AUTHORS.md)
for upstream credits. RyGT7 changes build on the work of the ryGT, Ryty, shadGT, shadPS4, and third-party
dependency contributors. GPL version 2 applies to this project; individual files and dependencies
retain their stated licenses. [LICENSE](LICENSE) and the release's `licenses` folder contain notices.

Gran Turismo is a trademark of Sony Interactive Entertainment. This project is independent of Sony
and Polyphony Digital.
