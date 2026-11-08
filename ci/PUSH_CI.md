# GitHub push builds

The workflows in this repository run on pushes to `main` and can be started manually.
Renderer pushes call these same workflows with their exact renderer SHA. Source and renderer
revisions are pinned once per run, then the controller waits for those revisions to reach GitLab
before building or publishing. There are no scheduled jobs.

Publication uses the repository secret `GITLAB_PACKAGE_TOKEN`, a project-scoped deploy token
with read/write package-registry access. It has no repository or API write permission.
