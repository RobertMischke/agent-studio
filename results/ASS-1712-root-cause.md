# ASS-1712 — In-Progress tasks show only ONE commit (per-task-worktree)

## Symptom
Tasks in lane `3-progress` (per-task worktrees, `maxParallelism > 1`) show 0–1
commits in Task-Detail instead of the real per-run commit history of the
`task/<id>` branch.

## Data-first reproduction (live workspace, 2026-06-09)
Read raw `logs/session-events.jsonl` (`HeadShaBefore..HeadShaAfter`) and
`task.json` `commits[]` for every 3-progress task:

| Task     | runs | recorded ranges (before..after)                          | commits surfaced |
|----------|------|----------------------------------------------------------|------------------|
| ASS-909  | 13   | every event `X..X` or `X..null`                          | 0                |
| ASS-1656 | 3    | `171a..171a`, `171a..171a`, `f17e..null`                | 0                |
| ASS-1662 | 4    | two `570f..570f`, one real `9eef..9b87`, `9b87..null`   | 1                |
| ASS-1700 | 1    | `9b87..null`                                            | 0                |

ASS-909 ran **13 times** yet surfaced **0** commits.

## Root cause (confirmed)
`/api/tasks/{id}/commits` (`TaskCommitsAggregator`) derives the commit list from
two sources, both of which are empty/collapsed for an in-progress worktree task:

1. **Per-run SHA ranges** `HeadShaBefore..HeadShaAfter`. These are captured from
   the **shared checkout HEAD** (`SafeGetHeadSha` / `ReadHeadShaAt(Entry.RootPath)`
   = `develop`), not the task branch. The agent's commits live on `task/<id>` in
   the worktree; `develop` only moves when integration fast-forwards it. Most runs
   therefore record `before == after` (or `after == null`), and the aggregator
   skips every trivial range — collapsing to 0–1 commits.
2. **Persisted `commits[]` attribution chain.** Attribution only runs when a task
   *leaves* 3-progress, so an in-progress task's chain is empty (or holds just the
   singular auto-commit snapshot).

The task branch itself cannot recover the history either: `direct-merge`
integration **rebases `task/<id>` onto develop then fast-forwards develop**
(`WorktreeTaskLifecycle.Integrate`). After each run the branch equals develop's
tip, so `develop..task/<id> == 0` and earlier runs' commits are absorbed into
develop's mainline — indistinguishable without a per-task marker.

## The recoverable source
Every per-run worktree commit is stamped by `CrashRecoveryCommit` with a durable
trailer in its body (`ProjectRunner.IntegrateWorktreeRunAsync`):

```
<task title>

[parallel-slot worktree run; jobId=<task-id>]
```

This trailer **survives rebase + fast-forward** and uniquely identifies the task.
Grepping the integration history reconstructs the full per-task history:

```
$ git log --all --grep="[parallel-slot worktree run; jobId=<id>]" -F --no-merges
ui-navbar-lane-counters...              -> 3 commits  (3 runs)
slice-1-per-run-commit-step-artefakte   -> 3 commits  (3 runs)
arch-lane-keys... (ASS-1662)            -> 1 commit
```

Note: the internal `jobId` is the **title slug** (= `task/<slug>` branch suffix),
not the `ASS-####` key.

## Fix
Add a third, durable source to the commits endpoint: reconstruct the task's
commits from the run-tag trailer.

- `GitService.GetTaskRunCommits(jobId, watchPath)` — `git log --all --grep`
  (fixed-string) for the run trailer, parsed like the existing range query.
- `GitService.WorktreeRunCommitTrailer(jobId)` — single source of truth for the
  trailer, used by both the writer (`ProjectRunner`) and the reader (grep).
- `TaskCommitsAggregator.Aggregate(...)` — fold the reconstructed commits in,
  deduped by SHA, attribution overlay applied (RunIndex null).
- `TaskGitEndpoints` — wire the source into `/commits`, `IsKnownJobCommit`
  (drill-down validation), and the aggregate files/diff endpoints.

This is additive and dedup-safe: real per-run ranges and the persisted chain
still win where present; reconstruction fills the gap the collapsed ranges leave.

## Regression test
`TaskCommitsAggregatorTests.Aggregate_TaskBranchRunCommitsSurfaceWhenRangesCollapse`
— an in-progress task with all-trivial ranges + empty chain + 3 reconstructed
run commits must surface all 3 (was 0).
