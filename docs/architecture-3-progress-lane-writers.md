# 3-progress lane writers

The `<watchPath>/3-progress/` lane is touched by several independent
services. Each one performs structural directory mutations
(`Directory.Move`, `Directory.Delete`, rename + state-field rewrite). When
two writers operate on the same source slug concurrently, the result is
one of three symptom shapes:

- A reader catches a folder mid-rename and sees no `job.json`, then
  classifies it as an orphan.
- Two writers race to rename the same source; the second arrives after
  the first finished, the source slug is gone, and the collision handler
  produces a sibling with a `-2` suffix.
- The boot sweep runs concurrently with crash-recovery's transition
  completion and marks a job as stuck that another sweep is about to
  finish promoting.

This file is the canonical inventory of writers and serves as the load-
bearing reference for the per-project `LaneMutexRegistry` (F21).

## Writers

| Writer | Trigger | What it moves | Entry point |
|--------|---------|----------------|-------------|
| `JobTransitionService.MoveAsync` | API `POST /api/jobs/{id}/move`, runner completion, drag-and-drop in UI | Single job from any lane to any lane; combines move with auto-commit + reorder. | `backend/Services/Jobs/JobTransitionService.cs` |
| `JobTransitionService.BatchMoveAsync` | API `POST /api/jobs/batch-move` | Many jobs in one call (per-item atomic). Each item routes through `MoveAsync`. | same file |
| `JobStateMachine.MoveJob` / `MoveFolderToState` / `DeleteJob` / `ChangeProject` | Called by every higher-level mover. The lowest-level lane mutator. | Direct `Directory.Move` / `Directory.Delete`. | `backend/Services/Jobs/JobStateMachine.cs` |
| `CrashRecoveryService.RecoverAsync` | Boot (before first runner tick) | Finishes a transition whose `completion-marker.json` survived a backend crash. Calls `JobTransitionService.MoveAsync`. | `backend/Services/Runner/CrashRecoveryService.cs` |
| `StaleProgressArchiver.SweepAsync` | Boot (after crash recovery), and could be invoked later by the supervisor | Moves over-budget `3-progress` folders to `3a-failed-pickup` (orphan / empty), or finishes a missed `3-progress -> 4-auto-review` transition when the sentinel survived. Calls `JobTransitionService.MoveAsync` and `ITaskAccess.MoveOrphanToFailedPickup`. | `backend/Services/Runner/StaleProgressArchiver.cs` |
| `ProjectRunner` pickup / dead-letter | Per-project tick (~1 s) | Picks a job from `2-ready` into `3-progress`; dead-letters an over-attempt folder into `3a-failed-pickup`; cleans up post-move skeleton folders left behind by Windows file-handle races. Calls `JobStateMachine.MoveJob`, `ITaskAccess.MoveOrphanToFailedPickup`, and `ITaskAccess.DeleteLaneFolder`. | `backend/Services/Runner/ProjectRunner.cs` |
| `ITaskAccess.MoveOrphanToFailedPickup` / `DeleteLaneFolder` | Typed escape hatches for orphan moves (called by the runner and the boot sweep). | Move into `3a-failed-pickup`; recursive directory delete. | `backend/Services/TaskAccess/TaskAccessService.cs` |

## Boot sequence

`Program.cs` runs them sync (in order) so the runner sees a clean lane on
its first tick:

```text
1. JobStateMachine.EnsureStateFoldersAndMigrate   (idempotent folder migration)
2. CrashRecoveryService.RecoverAsync              (complete pending transitions, rescue orphan changes)
3. StaleProgressArchiver.SweepAsync               (move stale 3-progress folders to 3a-failed-pickup)
4. TaskRunnerService starts; per-project ticks begin
```

The order is load-bearing: the archiver classifies a folder as "stale"
on its activity mtime, which crash-recovery may have just bumped by
finishing the transition. Running the archiver first would dead-letter
folders that were one ms away from a clean promote.

## Serialisation: LaneMutexRegistry

`backend/Services/Jobs/LaneMutexRegistry.cs` holds a per-watch-path
`SemaphoreSlim(1,1)`. Every lane writer acquires the mutex around its
structural directory mutation. Acquire / release is leaf-level on
purpose: `JobStateMachine.MoveJob`, `MoveFolderToState`, `DeleteJob`,
`ChangeProject`, and `TaskAccessService.DeleteLaneFolder` each take the
mutex inside the method body. Callers higher up the stack
(`JobTransitionService`, `CrashRecoveryService`, `StaleProgressArchiver`,
`ProjectRunner`) **do not** acquire separately - their lane mutations
go through one of the leaf methods, so the call chain is always
one-acquire-deep. No re-entrancy bookkeeping is needed.

Trade-offs documented:

- **`SetOrderInLane` runs outside the mutex.** It rewrites every
  sibling's `order` field after a successful move. The worst case is a
  partial reorder if a concurrent lane writer moves a folder out
  during the rewrite; the next reorder corrects it. Holding the mutex
  across the field rewrites would extend the lock far past the
  rename window and defeat the "keep the lock tight" guideline from
  the F21 task notes.
- **Auto-commit runs outside the mutex.** The auto-commit step in
  `JobTransitionService.MoveAsync` operates on the project's git tree,
  not the lane folder. Holding the lane mutex while the commit runs
  would serialise commits unnecessarily.
- **Cross-project moves use a two-mutex ordered acquire.**
  `JobStateMachine.ChangeProject` takes both source and target mutexes
  in ordinal-lowercased ascending order so a simultaneous `A -> B`
  and `B -> A` move cannot deadlock.
- **Readers do not take the mutex.** `JobScannerService` is the canonical
  read path; it has its own cache and absorbs mid-rename windows by
  invalidating-and-rescanning. A concurrent reader will see either the
  pre-move state or the post-move state, never both.
- **Timeout 30 s, then proceed unprotected.** If the semaphore is held
  longer than 30 s (the legitimate move budget is sub-second), the
  registry logs a warning and the caller proceeds without exclusion. A
  timeout here is itself a bug signal: investigate before this becomes
  routine.

## Race scenarios that this closes

1. **Boot race**: `CrashRecoveryService` and `StaleProgressArchiver` both
   iterate `3-progress` at boot. Without the mutex, crash-recovery's
   `JobTransitionService.MoveAsync` and the archiver's
   `MoveOrphanToFailedPickup` could rename the same source folder
   simultaneously. With the mutex, the archiver always sees either
   the pre-move state (and skips, because the folder is fresh) or the
   post-move state (and skips, because the source is gone).
2. **API + supervisor race**: `POST /api/jobs/{id}/move` runs while
   the supervisor's archiver sweep starts. Same mechanism: the move
   APIs go through `JobStateMachine.MoveJob`, the sweep goes through
   `JobStateMachine.MoveFolderToState`. Both take the mutex.
3. **Runner cleanup race**: `ProjectRunner.MoveProgressOrphanToFailedPickup`
   tries to delete a post-move skeleton folder via
   `ITaskAccess.DeleteLaneFolder`; meanwhile a manual API move targets
   the same lane. `DeleteLaneFolder` acquires the mutex; the manual
   move waits behind it.
4. **Mid-rename read by the runner**: the runner's pickup loop walks
   `3-progress` while another writer renames a folder. The reader doesn't
   take the mutex, but the writer's rename is atomic at the file-system
   level - the reader either sees the source slug or the destination,
   never both. The mutex prevents the destination-slug-collision shape
   (`-2` suffix folders) from forming in the first place.

## Adding a seventh writer

Any new code path that performs `Directory.Move` or `Directory.Delete`
against the lane tree must:

1. Be whitelisted in
   [`backend.Tests/Architecture/JobFolderAccessIsolationTest.cs`](../backend.Tests/Architecture/JobFolderAccessIsolationTest.cs)
   or route through `ITaskAccess` / `JobStateMachine` (which already
   own the lane vocabulary).
2. Acquire the lane mutex around the structural mutation, either
   directly via `LaneMutexRegistry.Acquire(watchPath)` or implicitly by
   delegating to one of the existing leaf methods.
3. Be added to the writer table above.

The architecture test catches the first; this document catches the
second. The third is the contract that makes the system
re-derivable later.
