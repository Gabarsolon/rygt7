# GT7 supplemental libraries

These are source implementations and exported function names, not extracted game code.
`gt7_stubs.c` supplies unresolved imports and forwards available functions to their real runtime
libraries. It is installed as `libs/libSceTextToSpeech.prx`.
`libSceMbus.c` is the upstream catch-all stub implementation with Audio3d stubs removed, so those
calls reach the renderer's implementation instead of being silently intercepted.

From a Visual Studio x64 developer command prompt with LLVM and MSYS2 UCRT64 GCC on PATH,
run `build.cmd <output-folder>`. The source and module definitions are retained for rebuilding.
The generated DLL import libraries refer to open-source runtime libraries shipped with ryGT7.
GPL-2.0-only applies to these compatibility implementations.
