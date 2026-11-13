# Notes for agents working on ryGT7

## Renderer switches on the launcher's Advanced page

The launcher does not keep a list of the renderer's `RYGT_*` and `SHADGT_*` switches.
`SwitchWalker` in `loader/AdvancedPage.cs` reads `libs\libSceVideoOut.prx` as a file (it never loads it)
and lists every `"RYGT_..."` and `"SHADGT_..."` string the renderer passes to `getenv`. Descriptions
come from the same DLL: `"RYGTSW1|NAME|kind|group|default|values|description"` strings in shadgt7's
`src/video_core/rygt_switches.cpp`. See `loader/ADVANCED-PAGE.md`.

- **Adding a renderer switch:** add its description line in shadgt7's `src/video_core/rygt_switches.cpp`
  (format in shadgt7's `AGENTS.md`). Do not add switch names, tooltips or defaults to the launcher.
- **A variable the launcher always sets itself** (in `StartGame`): add it to
  `SwitchWalker.LauncherManaged` so the page does not offer it.
- The launcher compiles with the .NET Framework 4 `csc`: **C# 5** only (no local functions, `out var`,
  tuples or string interpolation). New `.cs` files go in `loader/build.cmd` and in the test build's
  file list in `loader/test-gt7.cmd` (CI runs it; a file missing there fails the build).

## Launcher version

The launcher is versioned on its own: `LauncherVersion.Value` in `loader/Version.cs`, with a
matching section in `loader/CHANGELOG.md`.

- **Bump only the patch number:** 0.1.0, 0.1.1, 0.1.2, and so on. Launcher changes count as minor
  updates, new features and new looks included. Do not raise the minor or major number unless the
  maintainer says so.
- Change `Version.cs` and the changelog's newest `## x.y.z - date` heading together:
  `ci/rygt7/tests/test_launcher_version.py` fails when they disagree.
