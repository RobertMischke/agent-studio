# ASS-1712 — Reissue finding: the committed fix never reaches a UI surface

## TL;DR
The backend reconstruction committed in `bdf329ca` is **correct and tested**
(14/14 aggregator tests green; live grep recovers real per-run commits). But it
is wired **only into the `/api/tasks/{id}/commits` endpoint family**, and **no
operator-facing UI surface consumes that endpoint for the displayed commit
list**. For the exact bug scenario — an in-progress per-task-worktree task whose
persisted `commits[]` chain is empty or singular — the reconstructed history is
therefore **never displayed**. The acceptance criterion ("Task-Detail zeigt die
vollständige Commit-Historie") is **not met by the committed code**, which is
why no honest screenshot/e2e of the fix can be produced.

## What IS verified (green)
- Build: `dotnet test backend.Tests` compiles clean (warnings only).
  Evidence: `results/test-evidence/aggregator-tests.txt`
- Tests: **14/14** `TaskCommitsAggregatorTests` pass, including the 3 new
  ASS-1712 regressions (`Aggregate_TaskBranchRunCommitsSurfaceWhenRangesCollapse`,
  `..._DoesNotDoubleCountWithRange`, `..._OverlaysAttributionWhenChainHasIt`).
- Data source is real: `git log --all --grep '[parallel-slot worktree run;
  jobId=<id>]'` recovers multiple per-run commits (e.g.
  `slice-1-per-run-commit-step-artefakte` → 3 commits).
  Evidence: `results/test-evidence/reconstruction-live-proof.txt`

## The gap (confirmed three independent ways)
The reconstruction (`GitService.GetTaskRunCommits` → `BuildJobCommitsAggregate`)
is referenced **only** in `backend/Endpoints/Tasks/TaskGitEndpoints.cs`:
`GET /commits` (bare list), `GET /commits/files`, `GET /commits/diff`, and
`IsKnownJobCommit` (per-SHA drill-down validation).

1. **The FE never calls the bare `GET /commits`.** `task.service.ts` only calls
   `/commits/files`, `/commits/diff`, `/commits/{sha}/files`,
   `/commits/{sha}/diff`, and `/runs/{n}/commits`. (Repo-wide grep: no consumer
   of the bare endpoint.)
2. **Every displayed commit list/count reads `TaskInfo.commits` / `.commit`,
   not the aggregator:**
   - git-pane chain strip: `GitPaneService.setJob` → `commitChain =
     info.commits ?? (info.commit ? [info.commit] : [])`.
   - task-detail header badge: `gitCommitCount(info)` =
     `info.commits?.length || (info.commit ? 1 : 0)`.
   - board card: `commitChainOf(job)` = `job.commits ?? [job.commit]`.
3. **The aggregate files/diff endpoints are unreachable for the bug scenario.**
   The git-pane only calls `getJobCommitFilesAggregate` / `...DiffAggregate`
   when `commitChain().length > 1` (i.e. when `TaskInfo.commits` already has
   ≥2 entries). An in-progress task with a collapsed chain has 0–1, so the
   broadened SHA set in `JobCommitShas` is never exercised either.

And `TaskInfo.commits` itself is built by `CommitAttributionRunner` **without**
the reconstruction parameter, and only when the task **leaves** `3-progress`.
So an in-progress per-task-worktree task's chain stays empty/singular and the
reconstructed history never lands on any surface the operator sees.

Net: the committed change is **inert at the UI** for the reported case.

## Why no visual evidence was (or can be) produced
A screenshot of task-detail for an in-progress collapsed-chain task still shows
**one commit** — the product does not render the reconstructed history. A
Playwright spec that *injects* a multi-entry `TaskInfo.commits` would only prove
the pre-existing multi-commit renderer works; it would **not** prove this fix,
and would be misleading. Honest visual proof requires the reconstruction to
actually reach a displayed surface, which it does not.

## What completion requires (a scope/design decision)
Pick where the reconstructed history should surface; each option differs in
blast radius, ordering/attribution semantics, perf, and which surfaces become
consistent:

- **Option A — detail endpoint (server-side, contained to Task-Detail).** Fold
  `BuildJobCommitsAggregate` into the `GET /api/tasks/{jobId}` (`getDetail`)
  response's `TaskInfo.commits` for in-progress tasks. Reaches the git-pane
  chain + header badge. Requires injecting `GitService` + `TaskSessionLog` into
  `TaskCrudEndpoints`, mapping `TaskCommitRecord → TaskCommitInfo`, fixing
  ordering (aggregate is newest-first; `TaskInfo.commits` convention is
  oldest→newest), and a `git log --grep` per detail open. Board card stays
  stale (uses the list endpoint).
- **Option B — FE git-pane sources the chain from `/commits`.** Have
  `GitPaneService` fetch the bare aggregator and use it for `commitChain` when
  richer than the persisted chain (error-tolerant fallback). FE-only; keeps
  server semantics; fixes the git-pane but not the header badge / board card.
- **Option C — jobs list projection / scanner.** Fold reconstruction into the
  `GET /tasks` projection so board card + badge + pane all agree. Most
  consistent but touches the scanner hot path (see the known
  `JobScannerService.FindJob` mtime side-effect) — highest risk.

**Recommendation:** Option A. It directly and minimally satisfies the stated
acceptance ("**Task-Detail** zeigt die vollständige Commit-Historie") with the
narrowest blast radius, and it builds on the already-correct backend
reconstruction. Decide ordering + whether finished tasks should also re-source
from reconstruction before implementing.

## Why this run blocks instead of claiming done
The committed work is correct foundational backend code, but the task's
acceptance is unmet and cannot be visually proven without the wiring decision
above. Making that cross-cutting choice (which surface, which endpoint,
ordering + attribution semantics) silently in a managed run is not appropriate.
Per the run rules ("if any item cannot be completed or verified, stop and end
with BLOCKED"), this run blocks for a human scope decision.
