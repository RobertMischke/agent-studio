# Publishing Workflows (concept)

Version: v1 (2026-07-10)
Status: PUB-1 (derivation + read-only badges) implemented. PUB-2 (publish
actions) is a separate, future card - nothing in this concept triggers a publish.

Operator intent: **dead simple sehen, dass etwas Publizierbares da ist - im
Prinzip nach jedem Task.** After a task lands, the operator should see at a glance
whether the project now has something worth publishing (a package release, a
website deploy) without opening a terminal or remembering per-project release
mechanics.

## 1. Scope and non-goals

- **In scope (PUB-1):** derive a project's publish targets from repository facts,
  compute a pending delta per target, and surface both read-only in the UI
  (Project Hub badges + an accepted-task chip).
- **Out of scope (PUB-1):** performing a publish, mutating the repo, reading
  operator settings, or calling the GitHub/registry APIs. Everything here is
  derived from the checked-out repository and is side-effect free.
- **Deferred (PUB-2):** the actual publish action (cut a tag, trigger the release
  workflow, redeploy the site). The badges are the trigger surface it will hang
  off later.

## 2. Target derivation (from repo facts, never settings)

A project can have zero or more **publish targets**. Targets are derived, per
project, from three repository facts - workflows, tags, and manifests - and never
from a stored operator setting.

### 2.1 Package target (npm / NuGet)

A package target is derived when the repository has:

1. a **release-triggered workflow** under `.github/workflows/` - a workflow
   triggered by a version tag push (`on: push: tags: ['v*']`) or a published
   release (`on: release: types: [published]`); and
2. a **publish step or manifest** identifying the ecosystem:
   - **npm** when a workflow step runs `npm publish` (or uses
     `JS-DevTools/npm-publish`), or a `package.json` is found; the source root is
     the manifest's directory and the package name is its `name`.
   - **NuGet** when a workflow step runs `dotnet nuget push` / `nuget push` /
     `dotnet pack`, or a packable `.csproj` is found (has `IsPackable`,
     `PackageId`, `GeneratePackageOnBuild`, or a `Version`, and is not a test
     project); the source root is the project's directory and the package name is
     its `PackageId`.

The **current version** is the latest `v*` tag with the `v` stripped (e.g.
`v0.3.1` -> `0.3.1`). A package with a release workflow but **no `v*` tag** has
never been published - see the first-publish special state (§4).

Reference layouts the derivation is pinned against:

- **coding-agent-runner**: a NuGet release workflow + a Pages deploy workflow,
  currently at `v0.3.1`. Derives a `NuGet 0.3.1` package target + a website
  target.
- **coding-agent-chat**: an npm release workflow + a Pages deploy workflow, never
  tagged. Derives an npm package in the first-publish-pending state + a website
  target.

### 2.2 Website target

A website target is derived when a workflow deploys a site: the modern Pages
actions (`actions/deploy-pages`, `actions/upload-pages-artifact`), the classic
`peaceiris/actions-gh-pages`, or a workflow whose filename names it
(`deploy-website.yml`, `pages.yml`). The website source folder defaults to
`website/`; when the upload action names a different `path:`, that path is used.

## 3. Pending delta (what changed since the last release/deploy)

For each target, the pending delta is the number of **merged mainline
(first-parent) commits on the integration branch, since the target's reference
point, that touch the target's path scope**. First-parent collapses each merged
task branch to one mainline commit, so the number reads as "how many tasks
touched this target since it was last shipped", not raw commit churn.

- **Path scope.** Package = the package source paths (the manifest's directory),
  minus the website folder and repo-meta folders (`.github`, `docs`,
  `.orchestrator`) that are never package source. Website = the website folder.
  So a website change never counts toward the package, and vice versa
  ("Package-Quellpfade vs. website/").
- **Reference point.**
  - Package: the last `v*` tag (`referenceKind: tag`).
  - Website: the tip date of a `gh-pages` deploy branch when one exists
    (`referenceKind: pages-branch`) - the only website-deploy record that lives
    in git; else the last release tag as a documented approximation
    (`referenceKind: release-tag`); else no baseline (`referenceKind: none`),
    because the modern `actions/deploy-pages` flow leaves **no** git marker. When
    there is no baseline the count is not asserted (null) and the UI stays quiet
    rather than inventing a number.
- **Quiet by default.** `pendingCount === 0` (nothing merged since the reference
  touched the scope) renders **no badge**. Silence means "nothing to publish".

### 3.1 UI surfaces (read-only)

- **Project Hub badge** (project overview): one badge per non-quiet target, e.g.
  `NuGet 0.3.1 → 4 tasks pending`. Green for a normal package delta, violet for a
  website delta, amber for the first-publish state.
- **Accepted-task chip** (kanban card + task detail): a `publishable: npm,
  website` chip on a 6-completed task, listing the targets whose scope that task's
  merged work touched. Derived by set-membership of the task's mainline anchor
  (its recorded develop-merge commit, else its last commit) against each target's
  pending commit set - so it is computed once per project, never per card.

## 4. Special state: first publish pending

A package with a release workflow / manifest but **no `v*` tag at all** has never
been published. There is no version and no meaningful delta baseline, so instead
of a count the Hub shows `<ecosystem> first publish pending (manual, operator)`
(amber). The first publish is intentionally a manual operator action -
coding-agent-chat is the reference case.

## 5. Implementation map

- Backend derivation: `backend/Features/Publishing/` -
  `PublishWorkflowParser` (workflow facts), `PublishManifestLocator` (npm/NuGet
  manifest + source root), `PublishTargetService` (targets + pending deltas,
  cached per project), `TaskPublishableService` (per-task chip fold),
  `PublishEndpoints` (`GET /api/projects/{project}/publish-status`).
- Git primitives (read-only): `GitService.GetLatestVersionTag`,
  `GetMainlineCommitsForScope`, `GetTipCommitDateUtc`.
- Snapshot fold: `publishTargets` on `GET /api/projects/{project}/snapshot`.
- Board fold: `TaskInfo.PublishSignal` on `/api/tasks` + `/grouped` + detail.
- Frontend: the overview publish badges
  (`project-detail` overview, `data-testid="project-publish-badges"`) and the
  card chip (`task-card`, `data-testid="task-card-publishable"`).

## 6. Honest limitations

- Website pending is only precise when a `gh-pages` deploy branch exists. The
  modern `actions/deploy-pages` deployment is not recorded in git, so PUB-1
  approximates the baseline with the last release tag and labels it as such
  (`referenceKind`), or stays quiet when there is no anchor. It never fabricates a
  website count from thin air.
- Derivation is heuristic over the workflow text (no YAML dependency, matching the
  rest of the codebase). It targets the common publish/deploy vocabularies; an
  unusual custom pipeline may not be recognised, which fails safe to "no target"
  (quiet) rather than a wrong badge.
