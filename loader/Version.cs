// SPDX-License-Identifier: GPL-2.0-only
// The launcher's own version (semantic versioning: MAJOR.MINOR.PATCH). It is separate from the
// game, emulator and release-package versions. Bump it with a matching section in
// loader/CHANGELOG.md; the CI tests check that the two agree.
[assembly: System.Reflection.AssemblyVersion(RyGT.LauncherVersion.Value)]
[assembly: System.Reflection.AssemblyFileVersion(RyGT.LauncherVersion.Value)]
[assembly: System.Reflection.AssemblyInformationalVersion(RyGT.LauncherVersion.Value)]

namespace RyGT
{
    static class LauncherVersion
    {
        public const string Value = "0.1.2";

        /// "ryGT7 launcher 0.1.0", the first line of each setup log.
        public const string Banner = "ryGT7 launcher " + Value;
    }
}
