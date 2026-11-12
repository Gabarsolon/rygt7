# RyGT7 CI worker

This separate workflow builds the public `main` branch of
https://github.com/RyGT-Team/rygt7. Each job checks out the exact resolved source
commit and the official `rygt-team/shadgt7` `main` HEAD once per run. It runs on GitHub main pushes, renderer reusable-workflow calls, or manually. Every requested run builds; the legacy `force` input is retained for caller compatibility. Pull requests validate the trusted controller tests without building or publishing. The RyGTS workflows
and their Release/Debug packages are independent.

## Outputs

The initial RyGT7 channel is a Release client package:

- GitHub artifact: `rygt7-ci-<full source commit>-renderer-<full renderer commit>`, retained for seven days.
- Files: `RyGT7-ci-<short source commit>.zip`, its `.zip.sha256`, and `CI-BUILD.json`.

No tag, release, changelog or announcement is created. There is no RyGT7 Debug
package until matching symbols and diagnostics have a validated packaging path.
Optional server/shadNet distributions and the optional NVIDIA plugin/SDK are not
included in this client build.

## Build and provenance

Linux builds and tests the converter from the selected source. Windows builds the
launcher, converter, native runtime, wheel probe, eight pinned renderer targets
and supplemental GT7 stubs. Tests include launcher/setup checks, the source
packager's synthetic test and the worker's package validation. This is compilation
and packaging validation, not an in-game or GPU performance test.

The controller resolves the official `rygt-team/shadgt7` `main` HEAD to a full
SHA once, alongside the RyGT7 `main` source SHA. Windows fetches that exact
renderer object; all build, assembly and validation checks use the same resolved
value. This CI policy overrides the renderer commit declared by the runtime's
`build-support/renderer.json`; its official repository/branch requirement is
still checked. The configured and fetched URLs in `BUILD_INFO.json` describe
the CI selection. Branch movement during a run cannot silently change its build.

GitHub artifacts include both full SHAs, so a renderer-only
change triggers a new build without overwriting a different source/renderer pair.
The archive filename stays `RyGT7-ci-<short source commit>.zip` within the artifact. The exact VMA patch and clean-source provenance checks remain enforced;
an upstream patch change intentionally fails until the adapter is re-audited.

The source release packager expects a complete runtime. A SHA-256 pinned official
RyGT7 2026-10-31 package supplies libraries not rebuilt by the current build and
the FSR redistribution files. Only explicitly allowlisted files are read from
that archive. Fresh runtime/renderer/stub outputs overwrite their matching base
files. The converter and wheel probe are source-built; GCC redistribution files
come from the same UCRT64 toolchain. That toolchain is pinned: MSYS2 UCRT64 GCC
16.2.0-6 and the packages it needs, plus the FFmpeg prebuilt, each by file name and
SHA-256 in `ci/rygt7/runtime-toolchain.json` (the same pins as RyGTS).
`ci/rygt7/runtime_toolchain.py` downloads, checks and unpacks them; MSYS2 itself only
provides its Unix tools. `python ci/rygt7/runtime_toolchain.py pin <date>` moves the
pins to newer packages. The package includes required notices and
the source's bundled offline patch and credits.

This pinned binary base remains hosted in the GitLab package registry. GitLab is
not used to fetch source code or publish CI output.

`BUILD_INFO.json` records exact source and renderer commits, runtime-base URL and
hash, toolchain and submodule information, per-file origins and hashes, and CI
control/run identity. The historical release's gameplay and build-provenance
claims are replaced with accurate CI notes. Game executables/data, firmware,
fonts, saves, personal settings, logs and shader caches are excluded.

## Code signing

CI Authenticode-signs the launcher with a self-signed certificate
(`CN=RyGT-Team, O=RyGT-Team`) before checksums and packaging, via
`ci/rygt7/sign-windows.ps1`. Set repository secrets `CODESIGN_PFX_BASE64` (base64 PFX) and
`CODESIGN_PFX_PASSWORD` to use one stable certificate for every build; without
them each run signs with a throwaway certificate. The signature is not trusted
by Windows unless the certificate is installed as trusted, so SmartScreen and
"Unknown publisher" warnings can still appear. Local builds sign only when
`RYGT_CODESIGN=1`.

## Artifact delivery

The controller validates archive identity, every file hash, safe ZIP paths and
build provenance before uploading the GitHub Actions artifact. The workflow
summary links to the artifact and exact source commits. Renderer pushes call
the runtime reusable workflow; no publishing token or scheduled polling is
required. The optional legacy GitLab secret input is ignored for compatibility.

Local checks:

```text
python -m unittest discover -s ci/rygt7 -p test_*.py
python -m unittest discover -s ci/rygt7/tests -p test_*.py
```

Set `RENDERER_SHA` to the resolved renderer SHA for direct package validation.
Set `GT7_SOURCE_ROOT` and `GT7_RENDERER_ROOT` to clean exact source checkouts to
include the real-packager synthetic integration test. The full Windows build
also needs the hosted UCRT64, Visual Studio x64 and pinned LLVM environment.
