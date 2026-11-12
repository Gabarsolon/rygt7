# Placeholder libraries

GT7 links against PS4 libraries that ryGT7 does not implement: NP and PSN dialogs, USB, VR,
camera, trophies, FreeType helpers and others. The converted game executable loads
`libs/<library>.prx` for each of them when it starts, and stops with "Failed to load module" if one is
missing. These placeholders provide them.

Each `<library>.nids` lists the functions the game imports from that library, one `<library> <NID>` per
line.
`generate.py` turns a list into C source and a module definition. Every function prints
`[stub] <library> <NID>` to stderr the first time it is called and returns 0. An empty list makes a
library that exports only `ryty_stub_none`: the game loads it but calls nothing in it.

They are the same lists as RyGTS's (`core/libs/placeholders` there), without `libSceMbus`, which ryGT7
builds from `runtime/gt7`; `libMozaSDK-PS4`, `libSceAppContentUtil` and `libSceVideoRecording` are
GT7-only and empty. Together they replace the placeholder binaries of the former October 31 runtime
archive, whose source had not been kept. To implement one of these libraries for real, add it under `core/libs/prx/` and delete its list here.

`libSceSsl2.prx` is not a placeholder: it is the PS4's second module name for the libSceSsl API, so the build
copies the NID-patched `libSceSsl.prx` to it (`core/libs/CMakeLists.txt`).
