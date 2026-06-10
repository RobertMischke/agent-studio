# ASS-1753 - Runner slot-accounting desync after restart + mode-flip to manual

Operator report (2026-06-10 ~20:10, directly after a deploy-restart):

1. `RunnerStatus` reported `occupied=1 / activeJobId=t6a` while 3-4 freshly
   picked tasks sat in `3-progress` (lastActivity 0-7 min) and 3 `claude.exe`
   processes were live. The new 3-progress badge feeds from the slot registry,
   so it falsely showed "kein aktiver Run" for tasks that were actually running.
2. `RunnerMode` repeatedly flipped back to `manual` / `auto-single` after the
   restart even though the operator had set `auto-continuous`.

This is a backend fix (no UI re-implementation). A running-backend visual capture
is not runnable in this managed run (the dev backend is off-limits/shared, and
booting it would auto-commit the tree), so the deterministic substitute is the
unit/regression suite below, which exercises the exact in-memory facts the
endpoint and badge read.

## Directive 1 - Slot-accounting reflects ACTUALLY running runs

**Root cause.** A backend restart constructs a fresh `ProjectRunner` with an empty
`ActiveRuns` registry. The base CLIs (Claude/Codex/Gemini) reap orphans on startup
rather than reattach, but the CLI router can still own genuinely-live runs (Copilot
reattaches; a process that outlived the restart is still tracked). The registry
therefore under-counts occupancy. At `MaxParallelism > 1` the sequential
`IsRunningForProject` guard is bypassed, so the picker could double-spawn against a
still-live task.

**Fix.** `ActiveRuns` is the single source of truth, so the recovery path now books
into it:

- `ICliExecutionService.RunningExecutions()` (new default seam) returns the
  router-tracked `(jobKey, CliExecution)` pairs whose process is alive and whose
  status is still `running`. Implemented over `_processes` in
  `CliExecutionServiceBase` and `CopilotCliService`.
- `ProjectRunner.RegisterRecoveredRun(jobId, cliType)` re-books one live run -
  additive only (`TryClaim`, never releases; release stays owned by the run-finish
  path), idempotent (no-op when the job already holds a slot), and emits a
  structured log line plus a `runner_slot_admission` timeline event marked
  `recovered=true`.
- `ProjectRunner.ReconcileRecoveredRunsIntoSlots()` runs **once per process**
  (boot window) from `TickAsync`: for each CLI it filters `RunningExecutions()` to
  this project's `"<watchPath>::"` jobKey prefix and re-books each live run. From
  then on the normal claim/release path keeps the registry accurate.

After recovery, `occupied == number of genuinely-tracked live runs` again.

## Directive 2 - auto-continuous survives the restart

**Root cause.** The update-service quiesces runners to `manual` before a deploy
(`PUT /api/runner/{project}/mode`) and restores them afterwards. It used the same
endpoint as an operator toggle with **no reason**, so the change classified as
`user` and was persisted as the operator's durable mode - clobbering
`auto-continuous`. Restore is best-effort and is skipped on a failed/early-returning
update, so the transient `manual` survived the restart and boot defaulted to it.

**Fix.** A durable intent, separate from the live mirror, plus reason plumbing so a
system flip never advances the durable value:

- `ProjectSettings.DesiredRunnerMode` (new) - the value boot restores from.
  `RunnerMode` stays the live mirror that the supervisor meta-cycle reads for
  drift detection.
- `ProjectSettingsService.SetRunnerMode(project, mode, source)` advances
  `DesiredRunnerMode` only for a `user`-sourced change; a `system` /
  `circuit-breaker` / `supervisor` flip updates `RunnerMode` only.
- `ProjectRunner.OnModePersist` now carries `(mode, source)`; `SetMode` classifies
  the source via `ClassifyModeSource`, which now maps a reason starting with
  `update-` (the quiesce/resume reasons) to `system`.
- The mode endpoint threads a `reason` (`SetRunnerModeRequest.Reason` ->
  `RequestModeChange`), and the update-service sends `update-quiesce` /
  `update-resume` (`BackendProbe.SetModeAsync(..., reason)`).
- Boot restore in `TaskRunnerService` prefers `DesiredRunnerMode` over `RunnerMode`
  (legacy records without it fall back to `RunnerMode`).

Net effect: a quiesce records the `manual` it imposed without erasing the operator's
durable `auto-continuous`, and boot comes back up in `auto-continuous`.

**Migration note.** Legacy `project-settings.json` records have no
`DesiredRunnerMode`; they fall back to `RunnerMode`. An operator may need one
re-toggle to record durable intent.

## Directive 3 - Badge robust against missing slot info

**Root cause.** `GetRunActivity` derived `SlotActive` solely from
`_activeRuns.Contains(jobId)`, which is empty right after a restart, so the
classifier painted a running task as `no-active-run`.

**Fix.** `TaskRunActivityClassifier.Classify` now treats a live CLI execution still
reporting `running` as an equally authoritative "this task is alive" signal: it
returns `Active` when **either** the slot is occupied **or** the execution is
running. This covers the window between restart and the one-shot reconcile, so the
badge reads "Run aktiv" instead of the false "kein aktiver Run". (Deliberately not a
new `unknown` Kind - acceptance asks for "Run aktiv", which Active-from-execution
delivers, and orphans with no live execution still correctly resolve to
`no-active-run`.)

## Acceptance mapping

| Acceptance criterion | Covered by |
| --- | --- |
| Running runs back in slots after boot (occupied correct) | `RunnerSlotWiringTests.RegisterRecoveredRun_BooksLiveRunsIntoSlots_OccupancyMatchesLiveRuns` |
| Mode stays `auto-continuous` across a system flip + restart | `ProjectSettingsServiceTests.SetRunnerMode_SystemFlipToManual_PreservesDurableDesiredIntent` (+ `..._PreservesDesiredAcrossReload`) |
| Badge shows "Run aktiv" for running runs | `RunnerSlotWiringTests.RecoveredRun_ClassifiesAsActive_SoBadgeReadsRunActive`, `TaskRunActivityClassifierTests.Active_when_execution_running_even_though_slot_registry_lost_it` |
| System flip classified `system`, operator toggle `user` | `ProjectRunnerModeTests.SetMode_UpdateQuiesceFlip_FiresPersistHookWithSystemSource`, `..._ApiToggle_FiresPersistHookWithUserSource` |
| Build + Tests green | see below |

## Green-gate

| Gate | Command | Result |
| --- | --- | --- |
| Backend build | `dotnet build backend/OrchestratorApi.csproj -c Debug` | **0 errors** (only pre-existing nullable/CA warnings) |
| Update-service build | `dotnet build update-service/UpdateService.csproj -c Debug` | **0 errors / 0 warnings** |
| Directive 1/2/3 + classifier tests | `dotnet test --filter "RunnerSlotWiringTests\|ProjectRunnerModeTests\|ProjectSettingsServiceTests\|TaskRunActivityClassifierTests"` | **80 passed / 0 failed** |
| Update-service integration (quiesce/resume reason plumbing) | `dotnet test --filter "UpdateServiceIntegrationTests\|UpdateVerifierTests"` | **16 passed / 0 failed** |

Full suite: 3173 passed / 13 skipped / **10 failed**. All 10 failures are
pre-existing and environmental - they reproduce identically on the clean `HEAD`
(`c599fb5c`) with this branch's changes stashed: `MergeEndpointsIntegrationTests`
(x3, git-merge integration), `CodePatternDriftAnalysisServiceTests`
(`...AgainstLiveDevCheckout`, reads the off-limits live dev checkout),
`TaskFolderAccessIsolationTest`, `ProjectChatMigrationTests`, plus three
timing-flaky perf/push/usage tests (`JobsEndpointPerfTests`,
`AutoPushStrategyTests`, `AdHocUsageBusParityTests`) that pass on isolated re-run.
None touch slot accounting or mode persistence.

## Files changed

Production:
- `backend/Features/Cli/Execution/ICliExecutionService.cs` - `RunningExecutions()` seam
- `backend/Features/Cli/Execution/CliExecutionServiceBase.cs`, `CopilotCliService.cs` - implementations
- `backend/Features/Runner/ProjectRunner.cs` - `RegisterRecoveredRun`, `ReconcileRecoveredRunsIntoSlots`, `OnModePersist(mode, source)`, `ClassifyModeSource` update-* -> system
- `backend/Shared/Models/RunnerStatus.cs` - classifier prefers a live execution
- `backend/Shared/Models/ProjectSettings.cs` - `DesiredRunnerMode`
- `backend/Features/Projects/ProjectSettingsService.cs` - source-aware `SetRunnerMode`
- `backend/Features/Runner/TaskRunnerService.cs` - persist `(mode, source)`, boot prefers DesiredRunnerMode
- `backend/Shared/Models/TaskRequests.cs`, `backend/Features/Runner/RunnerEndpoints.cs` - reason on mode endpoint
- `update-service/IBackendProbe.cs`, `BackendProbe.cs`, `UpdateOrchestrator.cs` - send update-quiesce/update-resume reason

Tests:
- `backend.Tests/RunnerSlotWiringTests.cs`, `ProjectRunnerModeTests.cs`, `ProjectSettingsServiceTests.cs`, `TaskRunActivityClassifierTests.cs`

## Re-verification (reissue run, 2026-06-10)

Completion-gate flagged the prior run for only *announcing* a build. Re-ran the
green-gate to confirm the committed work (05e2bb18) actually compiles and passes:

| Gate | Command | Result |
| --- | --- | --- |
| Backend build | `dotnet build backend/OrchestratorApi.csproj` | **0 errors** (4 pre-existing warnings) |
| Test project build | `dotnet build backend.Tests/OrchestratorApi.Tests.csproj` | **0 errors** |
| Update-service build | `dotnet build update-service/UpdateService.csproj` | **0 errors / 0 warnings** |
| Targeted regression tests | `dotnet test --filter "RunnerSlotWiringTests\|ProjectSettingsServiceTests\|ProjectRunnerModeTests\|TaskRunActivityClassifierTests"` | **80 passed / 0 failed** |
