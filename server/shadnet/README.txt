shadNet: the PSN sign-in server for online play
================================================

A prebuilt shadNet server (shadnet.exe with the Qt 6 libraries it uses), for hosting online play:
the launcher's Online page starts it from server\shadnet next to RyGT7.exe, beside RyGTServer.

- Program files only. Its data (db\ with the accounts, score_data\, tss_data\, shadnet.log) is
  made on the hosting PC and is not part of this folder.
- shadnet.cfg has its keys blank (MemberApiKey, AdminApiKey, RegistrationSecretKey, AdminsList):
  every host's must be their own. The launcher fills MemberApiKey and RegistrationSecretKey with
  random values the first time it starts hosting.
- Ports: TCP 31313 (sign-in) and 31315 (web API); see the repository's README.md, "Online play".
- Needs the Microsoft Visual C++ 2015-2022 x64 runtime (vc_redist.x64.exe from Microsoft) on
  PCs that do not have it yet.

Qt 6 (Qt6*.dll and the plugin folders) is used under the GNU LGPL version 3; see
https://www.qt.io/licensing/open-source-lgpl-obligations. icuuc.dll is ICU (Unicode license).
