# Commit-attribution discovery audit

> Catalog of every code path that attributes a git commit to a task, and proof
> that they all converge on one schema and one write surface. Companion to the
> ADR "Commit-Attribution-Regel" and the `card-commit-source-not-repo-head`
> rule in [code-patterns.md](code-patterns.md).

## Why this audit exists

Three field bugs motivated it:

1. **Repo-HEAD fallback.** Review-lane cards showed "main: 20 files" and a commit
   count sourced from the shared project's working tree / branch HEAD, not from
   the task's own commits. Because the project git summary is shared across every
   card, a task frozen in `4-auto-review` / `5-human-review` advertised whatever
   branch state another job had just produced.
2. **`commitCount > 0` but `commits[]` empty.** A separately persisted count
   could drift away from the actual chain.
3. **Zero-commit ambiguity.** A card with no commits in a review lane could be a
   correct analysis-only task or a lost / undiscovered commit, and the two looked
   identical.

The fixes make `commits[]` the single source of truth, derive the count from it,
add a scanner `codeActivityDetected` signal to disambiguate the zero case, and
consolidate every attribution path through one runner and one write surface.

## Schema: single source of truth

Persisted in each task's `job.json`:

| Field | Meaning | Source |
|-------|---------|--------|
| `commits[]` | The attributed commit chain, oldest to newest. Each `TaskCommitInfo` carries `attribution` (kind) and `confidence`. | Attribution write surface. |
| `excludedCommits[]` | Commits the rule engine or an operator removed, each `TaskExcludedCommitInfo` with a `reason`. | Same write surface. |
| `commit` (legacy singular) | Kept pointing at the newest attributed entry so old readers still resolve a commit. | Same write surface. |

Derived, never persisted separately:

- `CommitCount => Commits.Count` (`AgentTaskboard.Shared/Models/JobModels.cs`). It
  cannot diverge from the chain because it is computed from it. This closes bug (2).
- `codeActivityDetected` is a scanner-computed boolean
  (`TaskScannerService.DetectCodeActivity`), **not** a commit source. It only
  records whether any run moved HEAD or auto-committed, so the UI can tell an
  analysis-only no-op from a pending / lost commit. This closes bug (3).

## The shared engine

Every attribution path funnels through one orchestrator:

- **`CommitAttributionRunner.Run(info, watchPath, sessions, git)`**
  (`backend/Services/Jobs/CommitAttributionRunner.cs`) builds the candidate set
  from the task's own run windows: `RunTimelineBuilder.Build` from the session
  log, then `TaskCommitsAggregator.Aggregate` resolving each run's
  `before..after` SHA range via `git.GetCommitsInShaRange`. Candidates are
  enriched with the full commit body + merge flag, then handed to the pure
  `CommitAttributionService.Attribute` rule engine. Returns `null` when there are
  zero candidate commits (nothing to persist).

The candidate commits come from per-run SHA ranges, **never** from `git rev-parse
HEAD` or the working tree. That is the structural guarantee against bug (1) on the
backend. The runner reads (git + session log) but never writes; callers persist
the result, keeping the API-only job-folder rule.

## Attribution paths (every writer)

| # | Path | Trigger | Persists via | Notes |
|---|------|---------|--------------|-------|
| 1 | `TaskTransitionService.TryAutoCommitAsync` -> `SetJobCommitOnFolder` | `3-progress` -> `4-auto-review` move when `settings.AutoCommit` | `TaskMutationService.SetJobCommitOnFolder` | Creates the commit via `GitService.AutoCommitAsync` (the one HEAD-moving writer) and stamps the produced `TaskCommitInfo`. Pre-flight guard skips it when the dirty paths predate the task's first run. |
| 2 | `TaskTransitionService.RunCommitAttribution` -> `SetCommitAttributionOnFolder` | Same transition, immediately after path 1 | `TaskMutationService.SetCommitAttributionOnFolder` | Runs the shared runner over the just-stamped chain, replacing `commits[]` + `excludedCommits[]` with the attributed result. Emits the structured log `commit-attribution jobId=... attributed=... excluded=...`. |
| 3 | `JobGitEndpoints` GET `/api/tasks/{id}/commits` -> `TryBackfillAttribution` | Read of a legacy folder | `TaskMutationService.SetCommitAttributionOnFolder` | Lazy, idempotent backfill. No-op unless the lane is attribution-final (`AttributionFinalLanes`), both lists are empty, and `codeActivityDetected` is true. Runs the same runner; best-effort (failures swallowed, read never fails). |
| 4 | `JobGitEndpoints` POST `/{jobId}/git/commit-accepted-evidence` -> `SetJobCommitOnFolder` | Operator "commit accepted evidence" action | `TaskMutationService.SetJobCommitOnFolder` | Manual sibling of path 1: creates an auto-commit and stamps the same `TaskCommitInfo` shape, then logs a `[commit]` chat entry. |
| 5 | `TaskMutationService.ExcludeCommit` / `IncludeCommit` | Operator override (exclude / re-include / add) | `TaskMutationService.WriteCommitState` | Moves a SHA between `commits[]` and `excludedCommits[]` with a manual marker. Same schema, same file. |

### One write surface

Paths 1-5 all reach disk through `TaskMutationService.WriteCommitState` (directly
or via `SetCommitAttributionOnFolder` / `SetJobCommitOnFolder` /
`ExcludeCommit` / `IncludeCommit`). Each is a replace-all write of `commits[]` +
`excludedCommits[]` with the legacy singular `commit` kept in sync. There is no
other writer of a task's commit set, so every path produces a consistent schema
entry by construction.

## Read / render surfaces (must not re-introduce the leak)

| Surface | Commit source | Guard |
|---------|---------------|-------|
| Backend GET `/api/tasks/{id}/commits` | Persisted `commits[]` (after optional backfill) | Aggregates the stored chain, never HEAD. |
| Frontend card commit chain | `commitChainOf(job)` -> `buildCommitChainView` | Lanes gated by `commitChainVariant`. |
| Frontend card zero-commit badge | `commits[]` empty + `codeActivityDetected` | `buildCommitEmptyBadge`, review lanes only. |
| Frontend card git pill (`GitSummaryService`) | Live repo working tree, **not** task commits | `LANES_WITH_GIT` = `3-progress` only. |
| Task detail git pane | `commitChain` from `TaskInfo`; `git show <sha>` of own commits | Worktree view gated by `isActiveJob`. |
| Hygiene strip | Per-job hygiene endpoint | Scoped per task; not repo-level. |

`GitSummaryService` is the only render-side path that reflects repo HEAD; it is
legitimate solely for the `3-progress` working-tree pill and is gated behind
`LANES_WITH_GIT`. The drift rule `card-commit-source-not-repo-head`
([code-patterns.md](code-patterns.md)) statically flags any future board surface
that references it without that guard.
