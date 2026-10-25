# ryGT7 launcher

The Windows launcher is based on ryGT's launcher at upstream commit `a42bb529` (2026-10-24).
It uses Windows' .NET Framework 4.x and does not require Python for setup or play.

## Player setup

1. Extract the complete ryGT7 release to a writable folder, separate from the source game folders.
2. Run `RyGT7.exe`.
3. Select your decrypted **Gran Turismo 7 base game, CUSA24769**. Select the folder containing
   `eboot.bin`, `sce_sys/param.sfo`, `sce_module`, and `contents` with `gt.idx` and the `gt*.vol` archives.
4. If your update is already merged into that folder, leave **Update** empty. Otherwise select the
   extracted update folder containing its own `eboot.bin` and `sce_sys/param.sfo`.
   Update files override matching base files, including individual files inside `contents`.
5. Select your PS4 firmware's system modules and the fonts folder containing `font` and `font2`.
6. Press **Set up**. Once it finishes, press **Play**.

Use the same drive as your game dump for hard links, or Windows Developer Mode for symbolic links
across drives. Keep the source game/update folders in place after setup: their files are linked,
not copied. Source game files are never patched. Conversion fixes apply only to the temporary ELF.

Setup rejects other title IDs, malformed metadata, incomplete folders, and updates older than
the selected base/merged game. It converts into a staging folder first, preserving an existing
executable if conversion fails. When replacing `app0`, the previous tree is retained as
`app0.previous-<timestamp>`; source dumps and saves are left intact.

**Verified dump: the local CUSA24769 dump with `APP_VER` metadata `01.00`.** Separate-update selection and overlays are tested,
but later game updates have not been verified for conversion or gameplay. The known 1.00 conversion
fix is gated by the complete ELF's SHA-256 and cannot be applied accidentally to another executable.

The release must include the open-source converter, GT7 runtime libraries, renderer, and their
redistributable dependencies. Players provide all game files, firmware modules, and fonts.
No game, firmware, saves, GT Sport scripts, or game shader packs belong in a release archive.
The generic project icon is original artwork; GT7's own window icon and background are read from
the player's game files after setup.

## Settings

Display, FSR/DLSS, shader compilation, logging, controller bindings, ports, and wheel settings are
retained from the upstream launcher. GT Sport's offline livery/decal patch preparation, shader-pack
extraction, opening-movie patch, widescreen camera patch, wet-reflection toggle, half-rate mirror,
and old-renderer selection are removed. The inactive splash-screen precompile control is also
omitted for this renderer build. GT7 uses the native renderer and precise readbacks.

Paths are stored locally in `RyGT7.ini`. Logs, shaders, converted output, and saves stay in the
launcher folder. The log threshold is also mirrored to `RyGT.ini` for existing runtime libraries.
The current renderer internally uses `user/custom_configs/CUSA03220.json` as its
configuration alias; this is unrelated to the required game title ID.

## Build and check

Run `build.cmd <output-folder>` from a Windows command prompt. It builds `RyGT7.exe` using the
C# compiler included with .NET Framework; it does not copy runtime libraries or game files.

Run `test-gt7.cmd <temporary-output-folder>` for synthetic setup regression checks. Optionally
provide an unpatched verified ELF and the expected patched ELF as the second and third arguments
to verify the conversion fix byte for byte. The tests retain their temporary fixtures for inspection.

`RyGT7.exe --snapshot <output.png> home` renders the Home page; `settings` and `controls` select
the other pages. `RyGT7.exe --setup` performs setup using the paths in `RyGT7.ini`.

GPL-2.0-only, retaining the upstream launcher license.
