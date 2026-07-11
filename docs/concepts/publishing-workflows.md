<<<<<<< HEAD
# Publishing workflows — dead simple, task-anchored

**Status:** concept v1, 2026-07-10 — operator-requested ("vollständiges und
sehr gut durchdachtes Konzept"). Related:
[`engineering-workstream.md`](engineering-workstream.md) (self-provisioning
precedent), AGT-2028 (task-spawner step), AGT-2029 (task dependencies),
AGT-1999 (merge-step pushes develop).

## 0. The rediscovered ground truth (operator asked: "war das Magie?")

No magic — it was built on 2026-07-07 and documented; the pieces:

| Piece | Where | State |
|---|---|---|
| Hosting VM | Hetzner CX23 (Nürnberg), Caddy/systemd, `/srv/sites/<slug>/current`, SSH alias `agent-orchestrator-web` | live |
| Hosting meta-repo | private repo `agent-orchestrator-website` (`hosting/setup-vm.md`, `Caddyfile`, `sites.json`, scripts) — **this is the "Metaprojekt"** | exists, local checkout present |
| Site docs | `agent-studio-marketing/06-website-planung/04-infrastruktur-und-sicherheit/deployment-setup-agent-orchestrator-dev.md` | authoritative narrative |
| CAR NuGet | `.github/workflows/release.yml` → nuget.org via **Trusted Publishing (OIDC)**, triggered by version tag (`v0.3.1` current, csproj `<Version>` is source) | automated on tag |
| CAR website | `.github/workflows/deploy-website.yml` (→ VM `/runner/`) | automated |
| CAC npm | `release.yml` → npmjs via Trusted Publishing on tag; **one-time first publish must be manual** (`npm login`, v0.1.0) — still pending | blocked on operator |
| CAC website | `pages.yml` (GitHub Pages) + `deploy-website.yml` (→ VM `/chat/`) | automated |
| Studio consumes CAR | `PackageReference CodingAgentRunner 0.3.1` (NuGet, not project ref) | manual bump per release |

So publishing already IS tag-driven and keyless where it's set up. What is
missing is **visibility and a one-move trigger in the product** — today you
must remember the mechanics per repo.

## 1. Operator intent (anchors, 2026-07-10)

- Every project has publish workflows of different kinds (NuGet, npm,
  websites). "Wir brauchen ein Konzept und einen Weg, das ins User
  Interface zu kriegen."
- **Dead simple:** "Ich muss sehr, sehr easy sehen: hier ist irgendwas
  Fertiges, was ich publizieren könnte — das ist im Prinzip nach jedem Task
  der Fall. Dann brauche ich einen schnellen Weg, tatsächlich zu
  publizieren — optimal an einem Task."

## 2. Model: publish targets, derived not configured

A **publish target** is a per-project entity `{kind, name, currentVersion,
trigger, consumers}`. Targets are **derived from repo facts** (house rule:
convention over settings):

- `release.yml` with tag trigger → package target (npm/NuGet — kind from
  workflow content/package manifest); current version from latest `v*` tag.
- `deploy-website.yml` / `pages.yml` → website target.
- `sites.json` in the hosting meta-repo remains the hosting registry.

Manual configuration only as override (add/hide a target), never as the
primary source.

## 3. Publishable-state detection

Per target, the backend computes the **pending delta**: merged task-commits
on the release branch since the last published version/deploy that touch the
target's path scope (package source paths vs. `website/` etc.).

- Project level: a **publish badge** per target — "NuGet 0.3.1 → 4 merged
  tasks pending", "Website: 2 changes since last deploy". Zero pending =
  quiet (no badge noise).
- Task level: on acceptance, each task shows **which targets it made
  publishable** — a small chip on the accepted card / task detail:
  "publishable: npm, website".

## 4. The one-move publish (UI)

**Where:** Project Hub header (per-target rows) + the task detail of an
accepted task (chips → same action).

**Package targets** — deliberate versioning, guided but one move:
1. Click "Publish" → panel shows pending tasks since last version, proposes
   the next **semver** (patch/minor from the pending tasks' taskType mix;
   editable).
2. Confirm → backend bumps the version source (csproj / package.json),
   commits, creates + pushes the `vX.Y.Z` tag → the existing OIDC workflow
   does the rest. **No new publish mechanics — the product drives the
   proven tag path.**
3. Panel tracks the workflow run (status, link), reports the published
   version.

**Website targets** — no versioning ceremony: "Deploy" triggers the
workflow (`workflow_dispatch` or empty commit per repo convention), shows
run status. Optionally **auto** (see §5).

## 5. Automation ladder (per target, explicit setting)

1. **manual** — badge only, human clicks (default for packages).
2. **suggest** — like manual, plus the accept-flow surfaces "publish now?"
   right at the task (operator's "optimal an einem Task").
3. **auto** — publish/deploy on every relevant merge (sane for websites;
   packages stay ≥ suggest — versions are decisions).

## 6. Consumer chain (v2, composes with existing concepts)

After a successful package publish, the known **consumers** (Studio's
`PackageReference`, apps with npm dep) are stale. The task-spawner step type
(AGT-2028) covers this: spawn "bump CodingAgentRunner to 0.4.0" into the
consumer project, with a `waitsOn` the publish (AGT-2029). This closes
Robert's observed chain CAR-3 → AGT cost audit → website without manual
shepherding.

## 7. Non-goals

- No secret management in the product (Trusted Publishing stays keyless;
  the VM keeps its SSH keys outside the app).
- No replacement of the GH workflows — the product is a **cockpit over the
  existing rails**, not a second publish engine.
- The npm **first publish** of coding-agent-chat stays a one-time manual
  operator act (npm requires it before a Trusted Publisher can be attached).

## 8. Slices

| Slice | Scope | Gate |
|---|---|---|
| PUB-1 | target derivation + pending-delta detection + badges (project hub, accepted-task chips) — read-only | none |
| PUB-2 | publish actions: semver proposal, version-bump commit, tag push, workflow-run tracking; website deploy trigger; automation ladder setting | PUB-1 |
| PUB-3 | consumer-bump spawning via task-spawner + waitsOn | PUB-2, AGT-2028/2029 |

## 9. Executed instances (log)

- **2026-07-10 — CodingAgentRunner v0.5.0 → nuget.org** (release.yml, Trusted
  Publishing; tag pushed by the night operator). Contents: `ultra` reasoning
  level + gpt-5.6 family recognition (CAR-2), **CodingAgentRunner.Pricing**
  price-history catalog + cost API (CAR-3), Workstream frame onboarding
  (CAR-1). Consumer chain (manual until AGT-2028/2029 land): AGT-2025 bumps
  the Studio PackageReference, AGT-2027 moves cost displays to the Pricing
  API, WEB-4 updates the public website. This entry is exactly the kind of
  record the Workstream Log will carry automatically once EW-2 (collector)
  exists.
=======
# Publishing Workflows (concept)

Version: v2 (2026-07-11)
Status: PUB-1 derivation and PUB-2 guided publish actions are implemented.

Operator intent: **dead simple sehen, dass etwas Publizierbares da ist - im
Prinzip nach jedem Task.** After a task lands, the operator should see at a glance
whether the project now has something worth publishing (a package release, a
website deploy) without opening a terminal or remembering per-project release
mechanics.

## 1. Scope and non-goals

- **PUB-1:** derive publish targets from repository facts, compute the pending
  delta, and show Project Hub badges plus accepted-task chips.
- **PUB-2:** open a guided action panel from a badge, list the pending accepted
  tasks, suggest an editable patch/minor version, and drive the repository's
  existing tag or workflow-dispatch path.
- **Still out of scope:** storing registry secrets or implementing a second
  package publisher. Trusted Publishing remains keyless and the existing
  GitHub Actions workflow owns the actual publish.

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

For later releases, a feature task in the pending set suggests the next minor
version; a mix containing only bugs and chores suggests the next patch. The
operator can edit the suggestion before confirmation. Confirmation requires a
clean worktree, updates `package.json` or the packable `.csproj`, creates one
release commit and `vX.Y.Z`, then atomically pushes `HEAD` and the tag to
`origin`. The product does not run `npm publish` or `dotnet nuget push`.

Each target stores an automation mode: `manual`, `suggest`, or `auto`. Package
targets clamp `auto` to `suggest`; website targets may use all three modes.
The action panel is the shared operator surface for the suggestion and current
workflow result. Website `auto` subscribes to the accepted-task transition and
waits for that task's integration merge to appear in the website delta before
dispatching the existing workflow.

## 5. Implementation map

- Backend derivation: `backend/Features/Publishing/` -
  `PublishWorkflowParser` (workflow facts), `PublishManifestLocator` (npm/NuGet
  manifest + source root), `PublishTargetService` (targets + pending deltas,
  cached per project), `TaskPublishableService` (per-task chip fold),
  `PublishEndpoints` (`GET /api/projects/{project}/publish-status`).
- Backend actions: `PublishActionService` performs guarded manifest/tag pushes,
  GitHub workflow dispatch, and `gh api` run tracking. `PublishEndpoints` owns
  the panel, automation setting, trigger, and run-status routes.
  GitHub authorization comes exclusively from the operator-managed `gh` CLI
  session on the host. The product does not accept, inject, or persist a GitHub
  token; unattended hosts must authenticate `gh` outside Agent Studio.
- Git primitives (read-only): `GitService.GetLatestVersionTag`,
  `GetMainlineCommitsForScope`, `GetTipCommitDateUtc`.
- Snapshot fold: `publishTargets` on `GET /api/projects/{project}/snapshot`.
- Board fold: `TaskInfo.PublishSignal` on `/api/tasks` + `/grouped` + detail.
- Frontend: `project-publish-panel` turns the overview badges into the guided
  release/deploy flow; the accepted-task chip remains the per-task signal.

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
>>>>>>> origin/develop
