# Notes for agents working on ryGT7

## Launcher version

The launcher is versioned on its own: `LauncherVersion.Value` in `loader/Version.cs`, with a
matching section in `loader/CHANGELOG.md`.

- **Bump only the patch number:** 0.1.0, 0.1.1, 0.1.2, and so on. Launcher changes count as minor
  updates, new features and new looks included. Do not raise the minor or major number unless the
  maintainer says so.
- Change `Version.cs` and the changelog's newest `## x.y.z - date` heading together:
  `ci/rygt7/tests/test_launcher_version.py` fails when they disagree.
