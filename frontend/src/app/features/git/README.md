# git

Per-job + per-project git status types. Used by the git pane in job-detail and the hygiene strip.

## Public API

Imports via `from './features/git'`. See [`index.ts`](./index.ts).

Pure types only:

- `GitFileChange` — one file in a status report (path + change kind + line counts).
- `GitStatus` — git status snapshot for a job worktree.
- `GitProjectSummary` — aggregated git state for a project (branch, ahead/behind, dirty count).
- `GitHygieneStatus` — derived hygiene flags (committed / pushed / clean) for a finished job.
- `JobHygieneContext` — context the hygiene strip needs to render its three icons.
- `JobCommitInfo`, `JobCommitDetail` — per-commit metadata + full diff.

Project Hub Git View (AGT-1807):

- `GitProjectInventory` — read-only branch / worktree / recent-history inventory for one
  project. Mirrors backend `GitProjectInventory`; `isRepo === false` + `error` is the
  empty/error signal.
- `GitWorktreeEntry`, `GitBranchEntry`, `GitCommitEntry`, `GitBranchCategory` — inventory rows.
- `buildGitTree(inventory)` + `GitTreeGroup` / `GitTreeLeaf` node types — the pure model that
  groups the inventory into the Git View tree (worktrees, integration / feature / task
  branches, recent history). Unit-tested in `models/git-tree.model.spec.ts`.

## Where the consumers live

- The git pane component is in `features/job-detail/components/git-pane/`.
- The hygiene strip is in `features/job-detail/components/hygiene-strip/`.
- The Project Hub Git View is `features/project-detail/components/project-git-panel/`; its
  HTTP wrapper is `services/project-git.service.ts` (`/api/git/inventory`,
  `/api/git/project-commit/{files,diff}`). It reuses the shared diff renderer
  `components/diff-content/` (also used by the full-screen `StudioDiffViewComponent`).
- The HTTP wrappers are in `services/git-summary.service.ts` and `services/git-hygiene.service.ts`.
