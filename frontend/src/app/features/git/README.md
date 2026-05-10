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

## Where the consumers live

- The git pane component is in `features/job-detail/components/git-pane/`.
- The hygiene strip is in `features/job-detail/components/hygiene-strip/`.
- The HTTP wrappers are in `services/git-summary.service.ts` and `services/git-hygiene.service.ts`.
