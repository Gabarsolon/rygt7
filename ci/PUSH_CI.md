# GitHub push builds

The workflows in this repository run on pushes to `main` and can be started manually.
Renderer pushes call these same workflows with their exact renderer SHA. Source and renderer
revisions are resolved and verified against GitHub once per run before building or publishing. There are no scheduled jobs.

Validated CI packages are uploaded as GitHub Actions artifacts. The workflow
summary links to the artifact and exact source commits.

## Pull request compile checks

Pull requests compile their GitHub merge revision directly: the Linux converter and tests,
Windows runtime DLLs, converter, wheel probe, and launcher. GT7 also compiles its compatibility
libraries. These checks use read-only repository access, receive no package secrets, and never
upload or publish packages. They fetch source directly from GitHub.
