# Solution Workspace And Component Project Model

> **Status:** target design and delivery decomposition, 2026-07-13. This page
> describes intended behavior. Current behavior is called out explicitly where
> it matters for migration and compatibility.
>
> This design refines the repository cardinality in
> [Project Relationship Model And Branch-Aware Wiki](project-relationship-model.md).
> It keeps that concept's branch and revision provenance, but makes project
> identity independent from repository identity.

## Decision

One large product is represented by one **Workspace** containing sibling
**Component Projects**. For the Agent Studio solution, the target shape is:

```text
Agent Studio solution workspace
├── Agent Studio component project     task keys AGT-*
├── Task Server component project      task keys TSV-*
└── Agent Runner component project     task keys RUN-*

All three component projects
└── bind to the same monorepo today through separate RepositoryBindings
```

The workspace is the explicit aggregate. It is not a project, backlog, runner
target, repository, or inheritance parent. Component projects are siblings.
There is no recursive project hierarchy and there are no inherited execution
settings.

The model has these hard rules:

1. A workspace is the solution boundary and contains component projects.
2. A component project owns its task-key sequence, backlog, lifecycle settings,
   release and deployment targets, Runner assignment, health, and documentation
   context.
3. A task belongs to exactly one component project for its whole lifetime.
4. A cross-project Initiative references tasks, dependencies, and milestones.
   It never copies a task card and never owns Runner, pipeline, repository, or
   deployment settings.
5. Every board request names one scope: workspace aggregate, component project,
   Initiative, or saved view.
6. Project identity and repository identity are orthogonal. Several component
   projects may bind to one repository, and one component project may gain more
   bindings later without changing its identity.
7. A task initially selects exactly one primary RepositoryBinding. A later
   multi-repository task model is out of scope for this delivery.
8. Merge admission and integration leases are keyed by repository and target
   branch, never only by project. Two sibling projects sharing a monorepo must
   not integrate concurrently into the same branch.
9. Repository extraction changes bindings, not workspace, project, task, task
   key, Initiative, or milestone identity.

## Domain model

```text
Workspace 1
  ├── N ComponentProjects
  ├── N Initiatives
  └── N SavedViews

ComponentProject 1
  ├── N Tasks
  ├── N RepositoryBindings ── N:1 ──> RepositoryIdentity
  ├── 1 RunnerAssignment
  ├── N DeploymentTargets
  ├── 1 HealthPolicy
  └── 1 DocumentationContext

Task N ── 1 ──> ComponentProject
Task N ── 1 ──> primary RepositoryBinding
Initiative N ── N ──> Task, by stable task reference
RepositoryBinding N ── 1 ──> installation-wide RepositoryIdentity
```

### Workspace

`WorkspaceRecord` keeps its stable `ws-*` id and gains only solution-level
metadata. It owns membership and aggregate navigation, not child behavior.

```text
Workspace
  id
  displayName
  color?
  sortOrder
  solutionKey?
  createdAt
```

Workspace defaults may remain as explicit defaults, but effective component
settings are materialized or resolved through a documented precedence rule.
Nothing is inferred from a parent project because no parent project exists.

### ComponentProject

The current `ProjectRecord` becomes a `ComponentProject` in product language.
The persisted type can keep its current name during migration to avoid a
needless big-bang rename.

```text
ComponentProject
  id                         stable PROJ-NNN
  workspaceId
  displayName
  shortCode                  owns new task keys and monotonic sequence
  lifecyclePolicy
  runnerAssignment
  deploymentTargets[]
  healthPolicy
  documentationContext
  repositoryBindingIds[]
  archived
```

The project owns planning and execution policy. It does not own a repository
path. Local checkouts are Runner infrastructure, and repositories are reached
through bindings.

### RepositoryIdentity

A RepositoryIdentity is a stable logical Git repository, independent from a
checkout path, workspace, and project. It is installation-wide so two
workspaces that happen to bind the same repository cannot receive different
integration locks.

```text
RepositoryIdentity
  id                         stable REPO-NNN
  displayName
  canonicalOrigin?           redacted when returned or logged
  originFingerprint?         normalized, credential-free identity
  defaultBranch?
  provider?
  archived
```

Runner-local checkout resolution is separate:

```text
RepositoryCheckout
  repositoryId
  runnerId
  checkoutPath
  gitCommonDirectory
  observedOriginFingerprint?
  observedRevision?
  health
```

`checkoutPath` and `gitCommonDirectory` are replaceable infrastructure. Neither
is a project or repository identity.

### RepositoryBinding

A binding joins a component project to a repository and states which part of
that repository supplies the component's code and branch context.

```json
{
  "id": "RB-AGT-MONO",
  "projectId": "PROJ-001",
  "repositoryId": "REPO-001",
  "displayName": "Agent Studio monorepo scope",
  "role": "primary",
  "codeScope": {
    "workingDirectory": ".",
    "include": ["frontend/**", "backend/Features/Studio/**", "docs/**"],
    "exclude": ["runner/**"]
  },
  "branching": {
    "workingBranch": "develop",
    "integrationBranch": "develop",
    "releaseBranch": "main"
  },
  "active": true
}
```

Binding invariants:

- `projectId` and binding `id` are stable identifiers. During repository
  extraction the binding id stays stable while its active `repositoryId` may
  change through an audited binding revision.
- Exactly one active binding has `role=primary` for a repository-backed
  component project. Additional bindings are allowed for future component
  evolution, but one task still selects one primary binding in this delivery.
- Branch roles belong to the binding because branch names are repository
  context. Deployment definitions remain project-owned and reference a binding
  and revision when needed.
- Binding revisions are append-only audit records. Historical task provenance
  points to the resolved repository and binding revision, so editing the current
  binding cannot rewrite history.
- Two bindings may point at the same RepositoryIdentity with overlapping scopes.
  Overlap is visible and valid for shared infrastructure. It is never used to
  weaken repository-scoped integration serialization.

### codeScope contract

`codeScope` is a repository-relative description of the component's default
working area. It is context and containment evidence, not a separate checkout
or a security sandbox.

| Field | Contract |
|---|---|
| `workingDirectory` | One normalized repository-relative directory. The Runner starts the CLI here. |
| `include` | Non-empty ordered list of repository-relative glob patterns. |
| `exclude` | Optional ordered list applied after includes. |
| `caseSensitivity` | `repository`, `sensitive`, or `insensitive`; default `repository`. |

Paths use `/`, may not be absolute, may not contain `..`, and must remain inside
the resolved Git worktree after symlink resolution. A task may narrow its scope
inside the selected binding, but may not silently broaden it. The pre-step puts
the resolved scope in the run prompt, and the post-step reports files touched
outside it. Enforcement stronger than the current worktree containment check is
a separate security decision.

Documentation roots do not hide inside `codeScope`. The component owns an
explicit `DocumentationContext`:

```text
DocumentationContext
  repositoryBindingId
  docsRoots[]                 for example docs/, docs/task-server/
  promptRoots[]
  defaultBranchRole           normally working
```

This lets sibling projects share a monorepo while opening distinct Wiki and
prompt contexts.

### Task ownership and provenance

Every task persists both identities instead of relying on `watchPath` to infer
them:

```text
Task
  id
  key                         minted by owning ComponentProject
  projectId                   required, immutable
  primaryRepositoryBindingId  required for every new task
  ...existing task fields
```

At creation, the server validates that the selected binding belongs to the
selected project and is active. At first execution, or whenever a new run is
prepared, the existing branch-aware provenance is extended with the resolved
repository context:

```text
TaskRunRepositoryContext
  bindingId
  bindingRevision
  repositoryId
  originFingerprint?
  codeScope
  workingBranch
  integrationBranch
  releaseBranch
  baseRevision
  taskBranch?
  checkoutRole
  observedAt
```

This reuses the existing `TaskProvenance` branch, base, transition, merge, and
landed-state concepts. Repository id and binding revision are added to every
persisted provenance anchor and every branch-dependent API response. Historical
provenance never depends on the binding's current repository target.

Changing task ownership is not a normal mutation. The existing
`change-project` route is deprecated. Rehoming creates a new task in the target
project with `supersedes` or `relatedTo` references and archives the source only
after explicit operator confirmation. This preserves the meaning of both task
keys and backlogs.

### Initiative

An Initiative is a workspace-level planning projection over canonical tasks.

```text
Initiative
  id                         stable INIT-NNN
  workspaceId
  title
  description?
  milestoneIds[]
  taskRefs[]                 projectId + task id + stable task key snapshot
  archived

InitiativeMilestone
  id
  title
  dueAt?
  taskRefs[]
  sortOrder
```

Task membership is a reference, not a copied card. The dependency graph comes
from canonical `Task.references.dependsOn` edges, which already drive pickup.
The Initiative API resolves those edges against its members and returns open,
fulfilled, missing, and external-to-Initiative targets. Initiative records do
not duplicate scheduler-load-bearing dependency edges.

Component-local Epics remain component-local. An Initiative is not a renamed
Epic and does not run an Epic decomposition pipeline. It can reference the leaf
tasks produced by project Epics; Epic container cards are excluded from the
delivery denominator to prevent double counting.

### SavedView and BoardScope

Every board request carries a discriminated scope:

```json
{ "kind": "workspace", "workspaceId": "ws-agent-studio" }
{ "kind": "project", "projectId": "PROJ-001" }
{ "kind": "initiative", "initiativeId": "INIT-001" }
{ "kind": "savedView", "savedViewId": "VIEW-001" }
```

A SavedView stores a base scope plus filters and presentation preferences:

```text
SavedView
  id
  workspaceId
  name
  ownerClientId
  visibility                  personal | workspace
  baseScope                   workspace | project | initiative
  filters                     lanes, projectIds, tags, owner, text, waitsOn
  grouping                    lane | project | milestone
  sort
```

Saved views never become implicit inheritance. Resolving a saved view produces
one concrete board scope and one canonical task set.

## One monorepo, three sibling projects

The initial Agent Studio solution migrates to the following example. Exact
scope globs are confirmed during implementation against the live source tree.

| Component project | Stable identity and key owner | RepositoryBinding | Initial code scope | Project-owned lifecycle context |
|---|---|---|---|---|
| Agent Studio | Existing `PROJ-*`, existing `AGT-*` keys | `RB-AGT-MONO -> REPO-MONO` | `frontend/**`, Studio-facing backend composition, shared docs | Studio backlog, app release target, assigned Runner, Studio health and Wiki roots |
| Task Server | New `PROJ-*`, `TSV-*` keys | `RB-TSV-MONO -> REPO-MONO` | task API/store, registry, server contracts, shared models needed by the server | Server backlog, Task Server deployment target, server health and docs |
| Agent Runner | New `PROJ-*`, `RUN-*` keys | `RB-RUN-MONO -> REPO-MONO` | `runner/**`, runner protocol, lease and execution integration code | Runner backlog, daemon release target, Runner assignment and operating docs |

All three bindings resolve to the same repository and may resolve to the same
local clone on one host. Tasks stay in separate task stores and boards. A
Runner claim is project-scoped, but integration admission for all three uses:

```text
RepositoryAdmissionKey = (repositoryId, integrationBranch)
                       = (REPO-MONO, develop)
```

The current `(projectName, integrationBranch)` integration lease key must be
replaced. The in-process semaphore must also move from one `ProjectRunner`
instance to a shared repository admission service, or sibling projects can race
the same branch despite the server-side lease fix.

## API direction

### Registry and binding APIs

```text
GET    /api/workspaces/{workspaceId}
GET    /api/workspaces/{workspaceId}/projects

GET    /api/repositories?workspaceId=...
POST   /api/repositories
PUT    /api/repositories/{repositoryId}

GET    /api/projects/{projectId}/repository-bindings
POST   /api/projects/{projectId}/repository-bindings
PUT    /api/projects/{projectId}/repository-bindings/{bindingId}
POST   /api/projects/{projectId}/repository-bindings/{bindingId}/retarget
```

`retarget` is explicit and audited. It validates started tasks and records a
binding revision instead of silently replacing historical provenance.

Project create accepts project-owned settings and either an existing
`repositoryId` plus `codeScope`, or a request to register a new repository and
create its first binding. `repositoryPath` and `rootPath` remain compatibility
inputs only during migration.

### Task APIs

New task creation requires stable identities:

```json
{
  "projectId": "PROJ-002",
  "primaryRepositoryBindingId": "RB-TSV-MONO",
  "title": "Make integration admission repository-scoped",
  "promptMarkdown": "..."
}
```

The response returns `taskId`, `taskKey`, `projectId`, and the binding id. A
missing project selection is a `400`; the server never falls back to the first
registered project. A binding outside the project is a `409`.

Read models replace path-based navigation fields with:

```text
project { id, shortCode, displayName, color }
repositoryContext { bindingId, repositoryId, codeScope, branchContext }
```

Deprecated `project` short-code and `watchPath` parameters continue to resolve
through the registry for one compatibility window. New clients send ids.

### Board query API

One query endpoint prevents four scope implementations from drifting:

```text
POST /api/boards/query

{
  "scope": { "kind": "workspace", "workspaceId": "ws-agent-studio" },
  "filters": {},
  "grouping": "lane"
}
```

The response contains the resolved scope, project summaries, lane groups, task
items, counts, and a scope revision. Tasks are keyed by `(projectId, taskId)` and
deduplicated before grouping. Every header total is computed from the returned
visible children, preserving the aggregate sum invariant.

`GET /api/tasks/grouped` remains as a compatibility projection over an explicit
workspace or project scope. An unscoped global all-workspaces board is retired.

### Initiative and saved-view APIs

```text
GET/POST       /api/workspaces/{workspaceId}/initiatives
GET/PUT/DELETE /api/initiatives/{initiativeId}
PUT            /api/initiatives/{initiativeId}/tasks
PUT            /api/initiatives/{initiativeId}/milestones
GET            /api/initiatives/{initiativeId}/rollup

GET/POST       /api/workspaces/{workspaceId}/saved-views
GET/PUT/DELETE /api/saved-views/{savedViewId}
```

Initiative task writes validate same-workspace membership. Rollups resolve
archive-inclusive task state and canonical dependencies on read. Unknown task
keys remain visible as missing references rather than disappearing.

### Repository-scoped run and integration APIs

Run claims remain project-owned because the project owns the Runner assignment
and backlog. Once claimed, the Task Server resolves the task's binding and sends
the Runner a repository context.

Integration lease and merge-queue APIs change from project names to stable
repository identities:

```json
{
  "repositoryId": "REPO-MONO",
  "integrationBranch": "develop",
  "bindingId": "RB-RUN-MONO",
  "projectId": "PROJ-003",
  "taskKey": "RUN-12",
  "runnerId": "agent-runner-01"
}
```

The lease key is only `(repositoryId, integrationBranch)`. `bindingId`,
`projectId`, and `taskKey` are holder provenance, not partition keys. Branch
cleanup, merge status batching, push queues, build gates, and commit
attribution use the same repository identity.

## Persistence

The first delivery can preserve JSON-backed registries while creating clean
domain boundaries:

| File | Versioned content |
|---|---|
| `.metadata/workspaces.json` | Workspace solution records. |
| `.metadata/projects.json` | Component projects, key counters, workspace membership, project-owned policy. Legacy repository fields remain read-only during migration. |
| `.metadata/repositories.json` | Installation-wide repository identities and credential-free origin fingerprints. Workspace visibility is derived through project bindings. |
| `.metadata/repository-bindings.json` | Active bindings plus append-only revisions and code scopes. |
| `.metadata/initiatives.json` | Initiative identity, task references, and milestones. No card copies. |
| `.metadata/saved-views.json` | Shared saved views; personal views may be partitioned by owner client id. |
| `job.json` | Adds required `projectId` and `primaryRepositoryBindingId`; existing key and references remain canonical. |

Cross-file writes use the existing bounded application service layer and an
atomic replace under one registry lock. A later database move may normalize
these records without changing the API identities.

Indexes are derived and rebuildable:

- `(workspaceId, projectId)` membership index.
- `(workspaceId, taskKey)` task-reference index, archive-inclusive.
- `(repositoryId, integrationBranch)` admission and merge-status index.
- `(initiativeId, projectId, taskId)` membership index.

No index is a second source of truth.

## Migration and compatibility

### Phase 0: additive readers

- Add RepositoryIdentity and RepositoryBinding readers without changing current
  runner behavior.
- Make all task reads expose resolved `projectId` even when legacy `job.json`
  only has `watchPath`.
- Add contract tests that reject `parentProjectId`, nested projects, and
  repository paths as identity.

### Phase 1: registry backfill

1. Keep every current workspace id and project id.
2. Reinterpret each current project as a component project. Display name,
   short code, task-key counter, task storage, settings, and URLs stay intact.
3. Deduplicate repositories using Git common-directory identity and a
   credential-free normalized origin fingerprint. Ambiguous local repositories
   require an operator choice and are never auto-merged by matching path text.
4. Create one primary binding per current project. Convert `RepositoryPath` to
   the repository checkout mapping and `RootPath` to the initial
   `codeScope.workingDirectory` where possible.
5. Backfill `projectId` and binding id into tasks through the Task Access API.
   A lazy read overlay keeps old folders readable until the sweep completes.

This migration is idempotent and writes a report with created identities,
deduplications, ambiguous rows, and task counts.

### Phase 2: split the Agent Studio solution backlog

- Retain the existing Agent Studio project and all existing `AGT-*` tasks.
- Create Task Server and Agent Runner as sibling component projects in the same
  workspace, with new `TSV` and `RUN` key sequences.
- Bind all three to the existing monorepo repository identity using reviewed
  code scopes.
- New work is created in the correct component backlog.
- Existing historical tasks are not silently reassigned. Active work that must
  move is reissued into the target component with explicit cross-references.

This avoids rewriting task keys or pretending historical execution belonged to
a component model that did not yet exist.

### Phase 3: client cutover

- Board tabs and URLs use stable scope and project ids instead of display names
  or `__all__` sentinels.
- The old `activeProjects` local-storage filter is imported once as a personal
  SavedView when it selects more than one project. An empty selection becomes
  the active workspace aggregate.
- Task creation removes first-project fallback and always shows component
  ownership.
- Name-based project routes, `watchPath`, `repositoryPath`, and `rootPath`
  remain deprecated adapters for one release and emit structured usage events.

### Phase 4: repository extraction rehearsal

Clone Task Server or Agent Runner into a separate test repository, retarget its
existing binding through the audited API, and verify:

- project id, short code, task keys, task storage, Initiative membership,
  milestones, URLs, and deployment identity do not change;
- old task provenance still resolves the monorepo repository id and revision;
- new runs resolve the extracted repository id and new origin;
- monorepo integration remains serialized across the bindings still sharing it;
- the extracted repository gets an independent admission key.

Only after this rehearsal passes should production extraction be attempted.

### Compatibility impact

| Current behavior | Compatibility path | Target |
|---|---|---|
| Project embeds `RepositoryPath` and `RootPath`. | Read as a synthesized primary binding and Runner checkout. | RepositoryIdentity, RepositoryBinding, and Runner checkout are separate. |
| Task project inferred from `watchPath`. | Resolve and expose `projectId`; backfill lazily and by sweep. | `projectId` is required and immutable. |
| Create may fall back to first project. | Warning during one window. | Missing project is a hard validation error. |
| `change-project` moves a card. | Deprecated and limited to a deliberate reissue flow. | Ownership is stable. |
| “All projects” can mean a global filter. | Map to the active workspace. | Explicit workspace aggregate. |
| Epics can be viewed across projects. | Keep current project-local Epic records. | Initiative is the cross-project planning construct. |
| Integration lease key is project name plus branch. | Dual-key telemetry during rollout. | Repository id plus branch. |
| Wiki task links show existence. | Reuse batch reference resolution. | Delivery projection shows missing, open, and done. |

## UX contract

### Workspace aggregate

The Explorer renders one expanded solution workspace with three sibling project
rows and explicit top-level entries for **Aggregate board**, **Initiatives**, and
**Saved views**. The aggregate board header reads `Agent Studio solution · All
components` and shows the scope control beside the title.

Each task appears once in its real lane and keeps a project badge. Selecting the
badge opens the component board; selecting the card opens the canonical task.
Project-specific Runner controls stay on project surfaces. The aggregate does
not invent one workspace Runner toggle.

### Component board

A component board shows only tasks whose immutable `projectId` matches the
scope. Its header exposes the component's Runner, health, release target, and
repository binding summary. Switching to Aggregate changes scope, not filters.

### Initiative rollup

An Initiative has one rollup header, milestone sections, project distribution,
and a dependency graph or list. Its board mode renders the canonical tasks once,
with project and milestone badges. Counts are derived from the visible leaf
tasks. Open or missing dependencies remain navigable and explain why a task is
waiting.

Initiative settings contain title, description, membership, and milestones.
They contain no Runner, pipeline, branch, deployment, or model controls.

### Saved scopes

The scope control lists workspace aggregate, sibling projects, Initiatives, and
Saved views in separate groups. Saving a view names the current explicit base
scope and filters. Opening it shows `Saved view: <name>` and a disclosure of the
resolved base scope so it can never masquerade as a project board.

### Task creation ownership

- From a component board, that project and its primary binding are preselected.
  The operator may change them before saving.
- From a workspace aggregate, Initiative, or SavedView, **Component project** is
  required. There is no first-project default.
- After project selection, **Repository binding** defaults only when the project
  has exactly one active binding. Otherwise it is required.
- Initiative and milestone membership are optional planning fields. They do not
  affect execution settings.
- The confirmation summary names task key owner, component backlog, repository,
  code scope, and Runner assignment before creation.

All surfaces are full-bleed, use calm background tint or badges rather than
left accent bars, work in both themes, and keep aggregate totals equal to their
visible children.

## Target-architecture delivery projection in the Wiki

A target-architecture page declares its delivery tasks explicitly in its
repository-owned companion metadata:

```json
{
  "deliveryProjection": {
    "taskKeys": ["AGT-2201", "TSV-1", "RUN-1"],
    "display": "milestone"
  }
}
```

The keys above are illustrative. The real decomposition workflow writes the
actual keys returned by the Task API after cards are created. It never invents
keys before the owning project issues them.

The Wiki read path batches those explicit keys through the workspace-scoped
task-reference resolver and returns, for each key:

```text
missing   key does not resolve in the workspace
open      task resolves and is not in completed/archive
done      task resolves and is in completed/archive
```

The row also carries component project, title, lane, milestone, waits-on state,
and navigation target. Unknown keys stay visible as missing. The page total is
the sum of the visible rows.

This projection is strictly read-only. Rendering or refreshing a Wiki page does
not create tasks, change lanes, add dependencies, add Initiative membership, or
rewrite sidecars. An explicit **Create missing task** action may open the normal
task-creation dialog in a later slice, but only an operator-confirmed Task API
request may mutate the board. The page is a declared projection over tasks, not
an alternate task store.

## Delivery decomposition

The following card aliases are stable planning handles. When the Task API is
available, create each card in the named component project, then replace the
aliases in the delivery projection with the actual cross-project task keys
returned by the API.

| Alias | Owning component | Deliverable | Depends on |
|---|---|---|---|
| `TSV-MODEL` | Task Server | RepositoryIdentity, RepositoryBinding, component-owned project fields, task ownership fields, JSON migrations, and compatibility readers. | none |
| `TSV-SCOPES` | Task Server | Typed board query, workspace/project scopes, SavedView persistence, deduplication, stable-id navigation payloads. | `TSV-MODEL` |
| `TSV-INIT` | Task Server | Initiative and milestone persistence/API plus workspace-scoped dependency rollup. | `TSV-MODEL` |
| `RUN-BINDING` | Agent Runner | Resolve selected bindings and code scopes into run context and provenance. | `TSV-MODEL` |
| `RUN-ADMISSION` | Agent Runner | Repository-scoped integration lease, merge queue, cleanup, push, merge status, and contention telemetry. | `TSV-MODEL`, `RUN-BINDING` |
| `AGT-BOARDS` | Agent Studio | Scope control, workspace aggregate, component board, stable-id tabs/routes, and no-duplicate rendering. | `TSV-SCOPES` |
| `AGT-CREATE` | Agent Studio | Explicit component ownership and binding selection in task creation. | `TSV-MODEL`, `AGT-BOARDS` |
| `AGT-INIT` | Agent Studio | Initiative rollup, milestone/dependency navigation, and SavedView UX. | `TSV-SCOPES`, `TSV-INIT`, `AGT-BOARDS` |
| `TSV-WIKI-DELIVERY` | Task Server | Read-only Wiki delivery-projection contract and batched cross-project resolution. | `TSV-MODEL`, `TSV-INIT` |
| `AGT-WIKI-DELIVERY` | Agent Studio | Wiki missing/open/done projection UI and task navigation. | `TSV-WIKI-DELIVERY`, `AGT-BOARDS` |
| `TSV-MIGRATE` | Task Server | Idempotent current-registry/task migration and Agent Studio solution split tooling/report. | `TSV-MODEL`, `TSV-SCOPES`, `TSV-INIT` |
| `RUN-EXTRACT` | Agent Runner | Separate-repository extraction rehearsal and cross-component end-to-end proof. | all cards above |

```text
TSV-MODEL
├── TSV-SCOPES ──> AGT-BOARDS ──> AGT-CREATE
│              └───────────────> AGT-INIT
├── TSV-INIT ──────────────────> AGT-INIT
│          └──> TSV-WIKI-DELIVERY ──> AGT-WIKI-DELIVERY
├── RUN-BINDING ──> RUN-ADMISSION
└── TSV-MIGRATE

all delivered slices ──> RUN-EXTRACT
```

### Executable card contracts and acceptance tests

#### `TSV-MODEL`: component and repository domain foundation

Implement the new records, registries, service boundaries, task fields, binding
revision audit, validation, and compatibility projection. Do not change board
UX in this card.

Acceptance tests:

- Three component projects can reference one RepositoryIdentity through three
  bindings with different code scopes.
- A task create without `projectId`, with a foreign binding, or with a recursive
  project field is rejected.
- Legacy project/task fixtures read through a synthesized binding without being
  rewritten on a GET.
- The migration is idempotent and preserves workspace ids, project ids, task
  ids, task keys, key counters, and settings.
- Binding retarget records an audit revision and leaves old run provenance on
  the old repository id.

#### `TSV-SCOPES`: explicit board and SavedView API

Implement `POST /api/boards/query`, typed scope validation, server-side task
deduplication, SavedView CRUD, and a compatibility adapter for grouped tasks.

Acceptance tests:

- Workspace scope returns the union of its component tasks exactly once.
- Project scope cannot leak a sibling project's tasks.
- Initiative and SavedView scopes resolve to canonical task identities.
- Every aggregate count equals the returned visible children.
- Unknown or cross-workspace scope ids fail without falling back globally.

#### `TSV-INIT`: Initiative model and dependency rollup

Implement Initiative/milestone CRUD and archive-inclusive rollups over canonical
task references. Reuse `TaskReferenceIndex` and `WaitsOnEvaluator`; do not add a
second dependency store.

Acceptance tests:

- One Initiative contains AGT, TSV, and RUN tasks without copying their cards.
- Moving a member task lane updates the next rollup without an Initiative write.
- Cross-project waits-on edges show open, fulfilled, missing, and
  external-to-Initiative targets correctly.
- Cross-workspace task membership is rejected.
- Initiative records expose no execution setting fields.

#### `RUN-BINDING`: binding-aware run preparation and provenance

Resolve the task's selected binding to a Runner-local checkout, start the CLI in
`codeScope.workingDirectory`, inject include/exclude context, and persist the
resolved repository context with existing branch provenance.

Acceptance tests:

- AGT, TSV, and RUN tasks sharing one clone start in their own configured
  working directories and retain their project identity.
- A task cannot broaden its binding scope; out-of-scope changes are reported.
- A missing or unhealthy checkout blocks preparation with repository and Runner
  diagnostics, not an “unknown project” error.
- An old provenance fixture remains readable; a new run contains binding,
  repository, branch, and base revision.

#### `RUN-ADMISSION`: repository-scoped shared-repo coordination

Replace project-keyed integration coordination with
`(repositoryId, integrationBranch)` across leases, local semaphores, merge
status, push, cleanup, and build admission.

Acceptance tests:

- Simultaneous TSV and RUN integrations into `REPO-MONO/develop` serialize.
- Two repositories using `develop` do not block each other.
- A stale fencing token from either sibling project cannot integrate, push, or
  clean up branches.
- Timeline and operator events identify repository, branch, project, and task.
- Existing single-project behavior remains unchanged.

#### `AGT-BOARDS`: aggregate and component board UX

Build the explicit scope control, stable-id tabs/routes, workspace aggregate,
and component boards against the board query API.

Acceptance tests:

- The Agent Studio solution Explorer shows Agent Studio, Task Server, and Agent
  Runner as siblings, never nested projects.
- The operator switches between one component and aggregate scope without
  changing filters.
- The aggregate renders each canonical task once with a component badge.
- Deep links survive project rename because they use ids.
- Playwright covers aggregate and project scopes in light and dark themes and
  persists screenshots under task results.

#### `AGT-CREATE`: explicit task ownership UX

Update every create entry point to require a component project and exactly one
primary binding. Remove first-project fallback.

Acceptance tests:

- Component-board creation is preselected correctly.
- Aggregate, Initiative, and SavedView creation cannot submit until a component
  is selected.
- Projects with several active bindings require an explicit binding.
- The confirmation names component, task-key prefix, repository scope, and
  Runner assignment.
- API validation errors roll back optimistic UI and remain accessible.

#### `AGT-INIT`: Initiative and SavedView UX

Render Initiative milestone and dependency rollups and the SavedView management
flow over canonical tasks.

Acceptance tests:

- The same task opened from an Initiative and its component board has one id and
  one live state.
- Milestone and total counts equal visible task rows.
- Missing and open dependencies are keyboard-navigable and status-correct.
- No Initiative screen exposes Runner, pipeline, deployment, or branch settings.
- Playwright covers cross-project rollup in both themes.

#### `TSV-WIKI-DELIVERY`: read-only delivery projection API

Extend Wiki companion metadata and its schema with explicit delivery task keys,
then resolve them in one archive-inclusive, workspace-scoped batch.

Acceptance tests:

- AGT, TSV, and RUN keys resolve with project identity and missing/open/done
  state in declared order.
- An unknown key remains as `missing`.
- Completed and archived tasks use the same terminal semantics as dependency
  fulfillment.
- GET and render paths perform no task, Initiative, lane, or sidecar mutation.
- Malformed optional metadata degrades to an empty projection without breaking
  the Wiki page.

#### `AGT-WIKI-DELIVERY`: Wiki projection UI

Render the delivery projection on target-architecture pages with component
badges, state, milestone, dependency status, and navigation.

Acceptance tests:

- Missing, open, and done rows are visually distinct without left accent bars.
- Totals equal visible rows and update after task state refresh.
- Selecting a resolved row opens the canonical task and component context.
- A missing row never creates a task merely by rendering or selecting it.
- Light, dark, keyboard, and narrow-width Playwright proof is captured.

#### `TSV-MIGRATE`: current workspace and Agent Studio solution migration

Ship a dry-run and apply migration, ambiguity report, rollback-safe backups,
and operator flow for creating the two new sibling components and reviewed
bindings.

Acceptance tests:

- Dry-run reports exactly one shared monorepo and three proposed bindings for
  the Agent Studio solution fixture.
- Apply is idempotent and a second run performs no writes.
- Historical AGT tasks stay in Agent Studio; new TSV and RUN keys start at their
  own monotonic counters.
- No task-store filesystem mutation bypasses Task Access.
- Ambiguous origin/path matches pause with an explicit operator decision.

#### `RUN-EXTRACT`: extraction rehearsal and end-to-end proof

Exercise the full model by retargeting one component binding from the monorepo
to a temporary extracted repository.

Acceptance tests:

- Project, task, task key, Initiative, milestone, and board URLs keep identity.
- Historical provenance points to the monorepo; new provenance points to the
  extracted repository.
- Remaining monorepo components still serialize integrations with each other,
  while the extracted repository integrates independently.
- Aggregate, component, Initiative, SavedView, and Wiki delivery scopes all
  remain status-correct with no duplicate cards.
- The test proves rollback by retargeting the binding revision, not by changing
  project or task ids.

## Release gates

1. Ship additive domain readers and migration dry-run first.
2. Do not enable multiple sibling component runners against a shared repository
   until repository-scoped admission is active.
3. Do not remove `watchPath` or path-based compatibility until frontend, Runner,
   and operator scripts emit no usage events for one release.
4. Do not call the solution split complete until the real Wiki projection lists
   actual AGT, TSV, and RUN task keys and shows their live missing/open/done
   states.
5. Do not extract a production repository until the extraction rehearsal passes
   without identity changes.

## Explicit non-goals

- Recursive projects, subprojects, portfolios-as-projects, or inherited project
  settings.
- Copying tasks onto Initiative boards or Wiki pages.
- Letting Wiki reads create or mutate tasks.
- Treating a workspace as a repository or merge queue.
- Treating code-scope overlap as sufficient integration coordination.
- Multi-repository execution inside one task in the first delivery.
- Rewriting historical task keys to match the new component names.
