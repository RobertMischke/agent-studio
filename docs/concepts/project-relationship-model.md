# Project Relationship Model And Branch-Aware Wiki

> **Status:** historical repository-centric discovery concept, 2026-07-11.
> This document does not describe shipped behavior. Its project/repository
> cardinality is superseded; its branch-provenance and checkout-isolation work
> remains useful.
>
> **Target clarification, 2026-07-13:**
> [Distributed Agent Studio target architecture](distributed-agent-studio-target-architecture.md)
> separates organizational component projects from repository bindings. The
> strict `Project 1 represents 1 Git repository` rule below is no longer the
> target for a monorepo containing Agent Studio, Task Server, and Agent Runner.
> Every cardinality, API shape, invariant, acceptance criterion, or non-goal
> below that depends on that rule is historical and must not guide new
> implementation. Branch-context, provenance, and checkout-isolation rules
> remain applicable.
>
> Visual companion: [relationship and Wiki checkout mockup](mockups/project-relationship-model.html).

## Historical decision summary

The following relationship spine records the superseded repository-centric
baseline. It is preserved to explain the branch-aware Wiki design, not as the
current project model.

The product has one durable relationship spine:

```text
Workspace 1 ── contains ──> N Projects
Project   1 ── represents ─> 1 Git repository
Repository 1 ── has ──────> N branches and N checkouts/worktrees
Branch    1 ── supplies ──> Wiki, Prompts, Tasks, URLs, and Project Hub context
```

Every project has repository-backed Docs, Wiki, and Prompts from the moment it
is created. The Wiki is not a second content store. It is the presentation and
editing surface for repository content, read through an isolated Wiki checkout.
Tasks remain application records, but their content and execution context is
anchored to a repository branch.

Branch context is a first-class part of provenance. Every branch-dependent
surface uses the same **Branch Context Control**, so a person can answer "which
branch supplies these data?" without learning a different badge language for
Wiki, Prompts, Tasks, URLs, and Project Hub.

## 1. Historical entities and invariants

| Entity | Cardinality and ownership | Durable responsibility |
|---|---|---|
| Workspace | Top-level surface; contains zero or more projects. A project belongs to exactly one workspace. | Organization, navigation, ordering, and workspace-level defaults. |
| Project | Belongs to one workspace and represents exactly one Git repository. | Product settings, working branch, branch model, checkout configuration, URLs, task configuration, and surface navigation. |
| Git repository | Exactly one canonical repository per project. | Versioned source, Docs, Wiki content, Prompts, branches, and history. |
| Branch | Many per repository. Branch roles are declared by the project's branching model. | A named line of content and execution provenance. |
| Checkout/worktree | Many per repository and always attached to a branch or explicit detached revision. | Filesystem projection for a specific purpose, never project identity. |
| Task | Application-owned record for one project. | Planning and execution state, with an explicit base/content branch and, when running, a task branch/worktree. |
| Project URL | Ordered project configuration entry. | URL plus the branch whose build or deployment it represents. |

Historical invariants:

1. **Superseded:** a project cannot aggregate several repositories. The target
   instead separates component-project identity from explicit repository
   bindings.
2. Moving a project between workspaces does not change its repository identity.
3. A checkout path is replaceable infrastructure. It must never become the
   stable identity of a project or repository.
4. Branch-bound content is never returned without branch and revision
   provenance in the API, even when the compact UI chooses not to show it.
5. The Wiki checkout, runner checkout, and task worktrees are separate roles.
   A refresh or branch switch in one role must not mutate another.

### Relationship versus presentation

The Wiki consists of two layers:

- **Repository layer:** the canonical repository, selected branch, commit,
  Docs tree, prompt files, and Git history. This layer answers where the
  information comes from.
- **Presentation layer:** navigation tree, Markdown rendering, HTML sandbox,
  metadata, search, history, and editing controls. This layer answers how the
  information is displayed and changed.

Presentation state may cache indexes and rendered output. It must not fork or
silently copy canonical content outside Git.

## 2. Branch roles and context resolution

Projects declare roles, not hard-coded branch names:

| Role | Typical branch | Meaning |
|---|---|---|
| Working branch | `develop` | Default source for project knowledge, prompts, new task bases, and the runner's working folder. |
| Running branch | `main` or `stable` | Branch represented by the currently running or released product instance. It may differ from the working branch. |
| Task branch | `task/AGT-1984` | Isolated execution branch created from the task's base branch. |
| Release branch | `main` | Curated release line in the configured branching model. |
| Viewed branch | User selection | Temporary read context for a surface such as Wiki or Git View. |

`develop` is a default example, not a global constant. The project's branching
model resolves branch roles to real names.

Each branch-dependent response carries one context envelope:

```json
{
  "branch": "develop",
  "role": "working",
  "revision": "8f21c4a",
  "repositoryId": "PROJ-001",
  "checkoutRole": "wiki",
  "observedAt": "2026-07-11T10:42:00Z",
  "freshness": "fresh"
}
```

Resolution rules:

1. An explicit surface selection wins for reading only.
2. Otherwise the project's configured working branch supplies Wiki and Prompts.
3. A task stores its base/content branch when created. Starting it may create a
   task branch from that exact revision; changing the project default later
   does not rewrite historical task provenance.
4. A URL stores its represented branch explicitly. No URL branch is inferred
   from whichever checkout happens to be active.
5. Project Hub overview uses the working branch as its primary context and
   exposes running-branch divergence. Git View remains the detailed inventory
   of branches, worktrees, HEAD, upstream, and history.
6. The configured working folder must be checked out on the resolved working
   branch before it becomes a runner source. A mismatch is shown as divergence
   and blocks new automatic execution until reconciled; it is never silently
   treated as if the folder were on the configured branch.

### Agent Studio special case

Agent Studio commonly runs from `stable` or `main` while work continues on
`develop`. Its default context is therefore:

```text
Running branch: stable
Working branch: develop
Wiki branch:    develop
Prompt branch:  develop
New task base:  develop
```

The running branch must not leak into knowledge resolution merely because the
backend process was launched from that checkout. Wiki content comes from the
configured working branch unless a user explicitly changes the viewed branch.

## 3. One Branch Context Control across the product

The repeated visual primitive is a compact control with a branch glyph, branch
name, and disclosure affordance. Its accessible name is always `Branch context:
<branch>`. It is a control, not a decorative status badge.

### Form and position

- On full surfaces, place it in the shared surface header, immediately after
  the title/path and before surface-specific actions.
- In dense rows, show only the branch glyph. Hover, keyboard focus, or the row
  detail view reveals the same control and complete provenance.
- The expanded popover always uses the same order: **Viewing**, **Working**,
  **Running**, then repository, revision, checkout, and freshness when relevant.
- Selecting another viewed branch changes only that surface's read context.
  Changing the project working branch is a separate, explicitly labelled
  project-setting action.

### Visual states

| State | Compact rendering | Expanded behavior |
|---|---|---|
| Viewing working branch | Neutral background, branch glyph, `develop`. | Explains that viewed and working branches match. |
| Viewing another branch | Soft informational background, `stable`, small `Viewing` label. | Shows `Viewing stable` and `Working develop` side by side. |
| Running/working divergence | Calm two-value summary: `Working develop · Running stable`. | Explains that Wiki, Prompts, and new task bases still use `develop`. Divergence is informational, not an acute warning. |
| Missing, stale, or failed source | Status dot plus explicit text such as `Refresh failed`. | Shows last successful revision, failure reason, and retry action. Only a current failure uses acute treatment. |

The control uses background tint, text, and a dot where needed. It does not use
a colored left accent line. It supports light and dark themes, keyboard access,
touch disclosure, and reduced motion.

### Surface application

| Surface | Default branch | Where the shared signal appears |
|---|---|---|
| Wiki | Working branch; user may select another viewed branch. | Wiki header beside document path; full checkout provenance in the popover. |
| Prompts | Working branch. | Catalogue/editor header; prompt detail inherits and repeats it near edit actions. |
| Tasks | Stored task base/content branch; running tasks also expose task branch. | Board/task header. Popover distinguishes `Based on develop@sha` from `Executing on task/...`. |
| Project URLs | Per-URL represented branch. | Branch glyph on compact Explorer URL row, visible detail in hover/focus card; full control in Project URLs settings. |
| Project Hub | Working branch. | Shared Project Hub header. Git View supplies the branch/worktree inventory underneath. |

URLs deliberately remain quiet during daily navigation. A preview link can show
`stable` while work uses `develop`, but that branch is revealed on hover, focus,
or detail rather than permanently appended to every URL label.

## 4. Repository-backed starter structure

New repository-backed projects receive a small, valid, intentionally empty
structure. Existing repositories get an opt-in scaffold action that never
overwrites files.

```text
<repository>/
├── README.md
├── docs/
│   ├── README.md              # Wiki landing page and docs index
│   ├── concepts/
│   │   └── README.md
│   └── operations/
│       └── README.md
├── prompts/
│   └── README.md              # Prompt catalogue landing page
└── .agent-studio/
    └── project.json           # Optional project hints, no secrets or absolute paths
```

Docs are the repository content; Wiki is their product presentation. The
starter `docs/README.md` explains this instead of creating a competing `wiki/`
content root. Prompt discovery begins at `prompts/`. Tasks are not scaffolded
as source folders because their lifecycle remains application-owned, but task
records always link back to the repository, base branch, and revision.

Local paths, credentials, refresh timestamps, and machine-specific checkout
state belong in the project registry, not `.agent-studio/project.json`.

## 5. Dedicated Wiki checkout

Each project can configure an isolated Wiki source with:

| Field | Meaning |
|---|---|
| Repository URL/origin | Canonical clone/fetch URL for the same repository represented by the project. |
| Checkout path | Local managed path. It must not equal or nest inside the runner checkout or a task worktree. |
| Selected branch | Defaults to the project working branch; can be changed from the Wiki UI. |
| Refresh policy | Manual plus a configurable interval. Refresh can be disabled. |
| Docs root | Repository-relative root, default `docs/`. |

The status contract includes origin, selected branch, resolved revision, last
attempt, last successful refresh, freshness, dirty state, and error detail.
The header reduces this to the Branch Context Control plus a calm freshness
label such as `Refreshed 4 min ago`; the full values live in its popover and
Wiki Source settings.

### Refresh and branch switching

1. Fetch origin without changing any runner or task checkout.
2. Resolve the configured remote branch to a commit.
3. Refuse destructive replacement when the Wiki checkout has uncommitted edits.
4. Materialize the new revision atomically, then rebuild search/render indexes.
5. Publish the new revision and refresh time together. Readers never see a
   branch label paired with content from the previous revision.

A branch switch follows the same flow and persists the Wiki's viewed branch.
It does not change the project working branch. A separate `Use as project
working branch` action may be offered only in Project Settings with an impact
summary for Prompts, new Tasks, and the working folder.

Editing keeps the existing explicit-save principle. Before a write, the UI
shows document, target branch, revision, and save strategy. Protected/shared
branches may require a docs branch or draft. Refresh is blocked while local
edits are unresolved, so content is never silently discarded.

### Failure and freshness states

| State | Reader behavior |
|---|---|
| Fresh | Render selected branch and revision normally. |
| Aging | Keep rendering last successful revision and show quiet age detail. |
| Stale | Keep last successful content, label it stale, and offer refresh. |
| Refresh failed | Keep last successful content, show current failure detail and retry. |
| Never synced | Show setup/empty state, not an empty Wiki that appears authoritative. |
| Dirty | Preserve edits, suspend automatic replacement, and require resolve/commit/discard choice. |

## 6. Project Hub integration

The existing Project Hub Git View already provides the correct detailed home
for repository path, current branch, branches, worktrees, upstream state, and
recent history. The relationship redesign extends rather than duplicates it:

- Project Hub shared header gains the Branch Context Control.
- Overview gains a compact Relationship summary: workspace, repository,
  working branch, running branch, Wiki checkout, and freshness.
- Git View remains the inspectable branch/worktree/history tree.
- Wiki Source settings own origin, checkout path, docs root, selected branch,
  and refresh policy.
- Project URLs settings add a required represented-branch field to each URL.

## 7. API and persistence direction

The singular repository fields below are the historical discovery shape. New
work must model them as one or more `RepositoryBinding` records as defined by
the distributed target. The remaining branch-context envelope still applies.

```text
ProjectRecord
  repositoryIdentity
  repositoryOrigin
  branchingModel
  workingBranch
  runningBranch?
  workingCheckoutPath?
  wikiSource { checkoutPath, origin, branchOverride?, docsRoot, refreshPolicy }
  urls[] { ..., representedBranch }

BranchContext
  repositoryId, branch, role, revision, checkoutRole, observedAt, freshness
```

The registry may store local checkout configuration, while API readers receive
resolved context. Branch names alone are insufficient for review or caching;
revision is required. Secrets embedded in origins are redacted from logs and UI.

## 8. Explicit follow-up cards

This discovery slice hands off exactly these separately tracked follow-up cards:

1. **Card C, Wiki Checkout:** implement the isolated Wiki source, refresh state
   machine, branch switching, atomic revision publication, dirty-checkout
   protection, and status API.
2. **Parallel WEB card, UI surfaces and Explorer tree:** implement the shared
   Branch Context Control across Wiki, Prompts, Tasks, Project URLs, and Project
   Hub; add represented-branch persistence and hover/focus disclosure for URLs;
   and implement the relationship and branch/worktree hierarchy in the Explorer
   tree. Publish the relationship-level website documentation as product proof,
   using the visual map as source material.
3. **Card D, Screenshots:** capture real-backend product-proof screenshots for
   matching and divergent branch states in both themes, including the Explorer
   tree and keyboard/focus disclosure for URLs. This follows the UI implementation
   in the parallel WEB card.

Foundation work needed by Cards C and WEB includes persistence, resolved
`BranchContext`, working/running roles, the starter scaffold, and the shared
control contract. The cards may sequence that foundation between them during
implementation; it is not a fourth follow-up deliverable from this discovery
slice.

## 9. Historical acceptance criteria for later implementation

- **Superseded:** a project cannot be configured with two canonical
  repositories. The current target tests explicit repository bindings and an
  unambiguous primary binding per simple run plan instead.
- Wiki reads `develop` while Agent Studio runs from `stable`, without touching
  the running checkout.
- Wiki, Prompts, Tasks, URLs, and Project Hub render the same Branch Context
  Control and vocabulary.
- A URL tied to `stable` reveals that association by hover, focus, and detail.
- Every content response identifies repository, branch, and revision.
- Refresh or Wiki branch switching cannot change the runner working folder or
  task worktrees and cannot discard dirty Wiki edits.
- The starter repository opens as a useful, honest empty Wiki and prompt
  catalogue without inventing a second knowledge store.
- Matching, divergent, stale, failed, and never-synced states work in light and
  dark themes and do not use colored left accent bars.

## 10. Explicit non-goals

- Implementing the checkout, refresh scheduler, or UI in this discovery slice.
- Treating a workspace as a Git repository or monorepo coordinator.
- **Superseded:** aggregating multiple repositories into one project. The
  current target permits explicit bindings and forbids only implicit aggregation
  without a declared run plan.
- Making the running branch the implicit source of truth.
- Auto-switching the project working branch when a person browses another Wiki
  branch.
- Replacing Git history with a Wiki-specific revision store.
