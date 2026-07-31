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

Project Hub Git tree:

- `GitProjectInventory` is the read-only branch, worktree, active checkout, and
  first commit-page projection for one project. `isRepo === false` plus `error`
  is the empty/error signal.
- `GitGraphCommit` carries parents, refs, linked task cards, deployment markers,
  and calculated develop/main presence. Presence comes from the same cached
  reachability resolver used by board merge status.
- `buildGitTree(inventory)` groups active local or remote leases, worktrees,
  integration, feature, task, and runner branches in the left repository tree.
- `buildGitGraphRows(commits)` assigns quiet SVG lanes from commit and parent
  SHAs. Older history comes from the bounded `/api/git/history` page endpoint.

## Where the consumers live

- The git pane component is in `features/job-detail/components/git-pane/`.
- The hygiene strip is in `features/job-detail/components/hygiene-strip/`.
- The Project Hub Git View is `features/project-detail/components/project-git-panel/`; its
  tree and graph render in dedicated child components. Its optional changes
  inspector fetches files and diffs only after an explicit click and reuses the
  shared `components/diff-content/` renderer.
- The HTTP wrapper is `services/project-git.service.ts`
  (`/api/git/inventory`, `/api/git/history`, and
  `/api/git/project-commit/{files,diff}`). The Git tree itself exposes no
  mutation control.
- The HTTP wrappers are in `services/git-summary.service.ts` and `services/git-hygiene.service.ts`.
