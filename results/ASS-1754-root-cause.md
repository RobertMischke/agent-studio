# ASS-1754 — Runs land via "Crash Recovery" instead of the regular completion path

## Symptom (reported)
Since ~18:30 on 2026-06-10 nearly every landing commit on `develop` shows
`author='Crash Recovery'`. The report inferred that claude processes were dying
before completion and that only the boot-time crash-recovery net was rescuing
their work.

## Data-first reproduction (live repo, 2026-06-10)
`git log --no-merges` on the integration branch:

```
c263528f | Crash Recovery | 22:10 | Nav-Umbau Schritt 3: Pipeline-Seite ...
f6d9ec30 | Crash Recovery | 21:31 | BUG: Slot-Accounting nach Restart desync ...
6ae79db8 | Crash Recovery | 21:22 | Nav-Umbau Schritt 3: Workflow/Lanes-Seite ...
...
b1996d66 | Robert Mischke | 21:03 | feat(board): refine task card signals   <- human
```

Two facts contradict the "boot-time rescue" theory:

1. **The messages are real task titles**, e.g. `Nav-Umbau Schritt 3: ...`.
   A genuine boot-time orphan rescue writes
   `chore(crash-recovery): rescue orphan changes for <id>` (those commits also
   exist in history — `24ef35d0`, `50e21a26`, … — and are correctly shaped).
2. **The bodies carry per-run trailers**:
   `[parallel-slot worktree run; jobId=…]` or `[conflict-resolution; jobId=…]`.
   Those trailers are written by the **runtime** worktree-integration path, not
   by `CrashRecoveryService`.

So the runs were **not** dying before completion — they were completing, but the
platform-owned landing commit was being **mis-attributed** to the Crash-Recovery
identity.

## Root cause (confirmed)
The per-run worktree landing commit reused `GitService.CrashRecoveryCommit`,
which hardcodes `--author="Crash Recovery <crash-recovery@agent-taskboard>"`:

- `ProjectRunner.IntegrateWorktreeRunAsync` (normal landing) — was line 933.
- the conflict-resolution commit — was line 1436.

`CrashRecoveryCommit` was chosen purely for its "add-all + fixed-body commit"
mechanics (see the prior `results/ASS-1712-root-cause.md`, which documents the
trailer that survives rebase+fast-forward). The author override was an
unintended side effect.

This stayed mostly invisible while worktree isolation was reserved for parallel
slots. **The "Always-Worktree durchziehen" change earlier the same day
(commit `904c1f0e`, 16:09) routed _every_ run through the worktree-integration
path** — so from then on *every* landing was stamped `Crash Recovery`, swamping
the log and making the regular completion path appear to have stopped.

The Crash-Recovery author is meant to be the boot-time exception net's marker;
once it appears on regular landings it becomes impossible to tell a genuine
rescue from a normal completion (acceptance: "Rescue NIE als regulären Landing
ausgeben").

## Fix
1. **Correct author on the regular completion path.** New
   `GitService.WorktreeRunCommit(project, repoRoot, message)` — identical add-all
   + fixed-body + SHA mechanics as `CrashRecoveryCommit` but with **no `--author`
   override**, so the landing uses the configured git identity. Both runtime call
   sites in `ProjectRunner` now use it. `CrashRecoveryCommit` is now reachable
   **only** from `CrashRecoveryService` (boot-time orphan rescue), so the
   Crash-Recovery author is reserved for the exception net. The durable
   `WorktreeRunCommitTrailer` is preserved, so ASS-1712 history reconstruction is
   unaffected.

2. **No-silent-death exit logging** (`ProjectRunner.OnCliFinishedAsync`). The
   finalize entry log now records `status + exitCode + duration` for every run,
   and the finalize `catch` (a throw here abandons completion/lane-move/re-issue
   and leaves the orphan changes the boot rescue later picks up) now logs the
   full exit context **plus the cause** instead of a context-free warning. The
   raw process exit was already logged at the CLI layer
   (`CliExecutionServiceBase.MonitorProcessAsync`); this closes the gap on the
   runner-finalize side.

3. **Resume-crash must not wipe `commits[]`** (`TaskMutationService`). A run that
   crashes seconds into a resume can drive the attribution post-step with an
   empty result; `WriteCommitState` is a replace-all write and would erase the
   task's landed-commit metadata. Added a no-wipe guard: refuse to shrink a
   non-empty persisted chain to empty (an empty attribution is never legitimate
   when commits already exist — the aggregator folds the persisted chain in). The
   guard fails open when there is nothing to protect.

## On the "multiple-rescue loop" (3× same task)
The repeated same-title landings (e.g. "Slot-Accounting nach Restart desync" at
21:09 / 21:12 / 21:31) are **incremental resume/reissue runs**, each landing its
own worktree commit — not three boot rescues. They were only *labelled* as rescue
because of the author bug. Restoring correct authorship makes a genuine rescue
distinguishable from a normal landing again, which is the prerequisite for any
future loop-breaking work; no speculative runtime change was made here without a
live repro.

## Acceptance mapping
- Exit-logging for every run (code + reason) — fix #2.
- Repro/cause documented + fixed — this doc + fix #1.
- One full task runs through the regular commit+completion path again with
  `author != Crash Recovery` — fix #1; pinned by
  `GitServiceWorktreeRunCommitTests.WorktreeRunCommit_UsesConfiguredIdentity_NotCrashRecoveryAuthor`.
- Resume-crash wipes no metadata — fix #3; pinned by
  `TaskCommitBindingTests.SetCommitAttribution_EmptyOverNonEmptyPersisted_RefusesWipe`.
- Build + tests green — backend.Tests build clean; the affected suites
  (GitService, commit-binding, crash-recovery, attribution, aggregation, runner)
  pass.

## Tests
- `backend.Tests/GitServiceWorktreeRunCommitTests.cs` — worktree landing uses the
  configured identity (not Crash Recovery); `CrashRecoveryCommit` still stamps
  Crash Recovery; clean tree → "Nothing to commit"; trailer survives.
- `backend.Tests/TaskCommitBindingTests.cs` — empty-over-non-empty refused;
  non-empty attribution still replaces; empty-over-empty fails open.
