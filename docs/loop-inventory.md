# Loop Inventory

Every place in the codebase where work can re-enter itself — retry, requeue, re-trigger, replay, recurring tick — is registered here. The inventory is the single source of truth for the loop-guard layer documented in [agent-contract-pattern.md](agent-contract-pattern.md) and decided by [ADR-0032](architecture-decisions.md).

When you add a new loop, the same commit must add:

1. An entry below in **Entries** with all fields filled.
2. A budget constant in code (named in the entry's `Budget` field).
3. A breaker test under `backend.Tests/Architecture/` (named in the entry's `Breaker test` field).

CI fails if any entry is missing one of the three. The check runs in `backend.Tests/Architecture/LoopInventoryConsistencyTest.cs` (every CI run, fast, no LLM).

A weekly LLM-driven discovery run (`LoopDiscoveryTest`, `[Trait("Category","Weekly")]`, gated on `LOOP_DISCOVERY=1`) scans the diff since the last green run for new candidate loops and writes proposals to `loop-inventory.md.candidates`. Proposals are committed by a human review, never auto-applied.

## Entry shape

Each entry uses the same fields:

```markdown
### <stable-id>

- **Kind:** Pre-Guard | Post-Guard | Tick | Watchdog
- **Where:** <file path or class.method>
- **Re-entry trigger:** what causes this loop to attempt to fire again
- **Budget:** named constant + default value (e.g. `PickupFailureThreshold = 3`)
- **Action when budget exhausted:** what the rule engine does
- **Breaker test:** path to the synthetic test that exercises the budget
- **Last fired:** ISO date + one-line context
- **Notes:** anything operator-relevant (e.g. counter persistence, reset rules)
```

`<stable-id>` is dotted: `<surface>.<what>` (e.g. `pickup.silent-runs-per-job`). Once published, the id is stable — never rename.

## Entries

### pickup.silent-runs-per-job

- **Kind:** Pre-Guard
- **Where:** [`backend/Services/Runner/ProjectRunner.cs`](../backend/Services/Runner/ProjectRunner.cs) `TryPickProgressJobOrDeadLetter` + `RecordPickupAttemptResult`
- **Re-entry trigger:** Pickup tick attempts to spawn the CLI for the same slug after a previous attempt produced zero stdout lines.
- **Budget:** `PickupFailureThreshold` (default 3 silent attempts per slug)
- **Action when budget exhausted:** Reroute the over-budget folder via `ProjectRunner.RerouteOverBudgetFolder` and append a row to `<workspace>/logs/pickup-failures.jsonl`. Per ADR-0051 there is no `3a-failed-pickup` dead-letter: a spawn failure (CLI never started) returns the task to `2-ready` and pauses the runner; a task-shaped failure (CLI ran but stayed silent) escalates to `5-human-review` and the runner continues.
- **Breaker test:** `backend.Tests/Architecture/PickupSilentRunsBreakerTest.cs` (to be added with the implementation task)
- **Last fired:** 2026-05-06 — 22-job drain caused by a 500-byte `claude.exe` stub from a broken npm postinstall. Detection worked correctly; the gap was the missing diagnosis step (now `pickup.diagnose-once-per-dead-letter`) and the missing cross-slug circuit breaker (`pickup.cross-slug-infra-circuit-breaker`).
- **Notes:** Silent run is defined as zero stdout lines, regardless of stderr or exit code. Stderr-only failures look identical to genuinely-quiet sessions; the diagnosis step disambiguates after the fact.

### pickup.cross-slug-infra-circuit-breaker

- **Kind:** Pre-Guard (cross-slug)
- **Where:** [`backend/Services/Runner/CrossSlugInfraCircuitBreaker.cs`](../backend/Services/Runner/CrossSlugInfraCircuitBreaker.cs); fed by `ProjectRunner.DeadLetterUnrecoverableFolder` (trip) and `ProjectRunner.RecordPickupAttemptResult` (productive-pickup reset); operator-resume reset hooked from `TaskRunnerService.SetMode`.
- **Re-entry trigger:** Two consecutive distinct slugs hit `pickup.silent-runs-per-job` for the same `(cliType)` within a sliding window.
- **Budget:** `CrossSlugInfraSilentLimit` (default 2 distinct slugs in 10 minutes for the same CLI). Configurable via `Supervisor:CrossSlugInfraSilentLimit` and `Supervisor:CrossSlugInfraSilentWindowMinutes`.
- **Action when budget exhausted:** Set the project's runner mode to `manual` (via the same `SetMode` path the API uses), append one row to `<workspace>/logs/infra-halts.jsonl`, emit one `[supervisor]` chat note on the freshly dead-lettered job, and halt the in-flight pickup tick mid-iteration so the remaining `3-progress` folders are NOT dead-lettered too. The diagnosis step (`pickup.diagnose-once-per-dead-letter`) still runs on the dead-letters that already happened.
- **Reset:** counter clears on (a) operator flip back to `auto-single` / `auto-continuous`, (b) any productive pickup (≥ 1 streamed CLI output line) on the same `(project, cliType)`, (c) 24 hours of inactivity (long-window cleanup).
- **Breaker test:** [`backend.Tests/Architecture/CrossSlugInfraCircuitBreakerTest.cs`](../backend.Tests/Architecture/CrossSlugInfraCircuitBreakerTest.cs), plus the unit suite in [`backend.Tests/CrossSlugInfraCircuitBreakerTests.cs`](../backend.Tests/CrossSlugInfraCircuitBreakerTests.cs) and the runner-integration coverage in `PickupLoopStrictIterationTests.CrossSlug_*`.
- **Last fired:** Not yet fired in production. The 2026-05-06 incident is the motivating example (would have stopped the drain at job 2 of 22).
- **Notes:** This is the loop guard the 2026-05-06 incident motivated. It deliberately distinguishes "this one job is broken" (per-slug counter, fine) from "the CLI itself is broken" (cross-slug counter, halt). Detection is deterministic: typed counter, config constants, single-state-machine mode flip - no LLM in the code path (ADR-0032).

### pickup.diagnose-once-per-dead-letter

- **Kind:** Pre-Guard (within the contract pattern's own Pre-Guard)
- **Where:** Diagnosis dispatcher invoked from `JobStateMachine.MoveFolderToFailedPickup` (to be added)
- **Re-entry trigger:** A job's dead-letter triggers a `pickup-failure-diagnosis` agent step. The Pre-Guard prevents a second diagnosis on the same slug, and across-slug rate limits prevent runaway agent invocations.
- **Budget:** `DiagnosisAttemptsPerSlug = 1`, `DiagnosisAttemptsPerHourPerWorkspace = 8`, `DiagnosisTokenBudgetCents = 50` per attempt
- **Action when budget exhausted:** Skip the diagnosis, write a `pickup-diagnosis-skipped` marker to the run folder, leave the dead-letter as-is (operator-resolved).
- **Breaker test:** `backend.Tests/Architecture/PickupDiagnoseBudgetTest.cs` (to be added)
- **Last fired:** Not yet implemented.
- **Notes:** A repeatedly-failing diagnosis is itself an infrastructure problem. We never call a second LLM to diagnose why the first one failed.

### pickup.requeue-after-diagnosis

- **Kind:** Post-Guard
- **Where:** Decider in `backend/Services/Pickup/PickupDecisionService.cs` (to be added)
- **Re-entry trigger:** Decider chooses an action that puts the slug back into `2-ready` (`task-bad-prompt` requeue, `task-env-missing` self-heal-and-requeue, `transient` retry).
- **Budget:** `RequeueAfterDiagnosis = 1` per `(slug, category)` per 24 hours; counter persisted to `<workspace>/state/loop-counters.json` (atomic-rename writes).
- **Action when budget exhausted:** Override the decider's chosen action to `escalate-human`; surface a banner with the per-slug counter contents.
- **Breaker test:** `backend.Tests/Architecture/PickupRequeuePostGuardTest.cs` (to be added)
- **Last fired:** Not yet implemented.
- **Notes:** Counter does not auto-reset on backend restart. The 24-hour window starts at the first fire, not at midnight.

### abort-review.rerun-per-job

- **Kind:** Post-Guard
- **Where:** [`backend/Services/Runner/PostAbortReview.cs`](../backend/Services/Runner/PostAbortReview.cs) (`PostAbortReviewDecider.Decide`), invoked from `ProjectRunner.OnCliFinished` on a non-clean run end and surfaced through `PostAbortReviewStepService`.
- **Re-entry trigger:** A CLI run ends non-clean (watchdog timeout, non-zero exit, unexpected stop). The abort-review agent judges the abort illegitimate and recommends `rerun` or `stronger-reissue`, which re-spawns the CLI for the same job instead of escalating.
- **Budget:** `PostAbortReviewDecider.DefaultRerunBudget` (default 2 automatic reruns per job).
- **Action when budget exhausted:** `PostAbortAction.EscalateHuman` — route the job to `5-human-review`. A null/unparseable verdict (CLI failure) fails closed to the same escalation regardless of remaining budget.
- **Breaker test:** [`backend.Tests/Architecture/AbortReviewRerunBreakerTest.cs`](../backend.Tests/Architecture/AbortReviewRerunBreakerTest.cs)
- **Last fired:** Not yet implemented in production (step is default-OFF per project via `PipelineStepConfigResolver.IsEnabled(settings, PipelineCatalogue.AbortReviewStep)`).
- **Notes:** The decider is pure (ADR-0032): the agent only classifies (`legitimate`, `recommendation`, `confidence`, `reason`); the binding action is computed in code from the recommendation plus the remaining budget. The budget counts down across reruns of the same job; the terminal state is always escalation, so the loop cannot run unbounded. `accept`/`human-review` recommendations bypass the budget (accept continues, human-review escalates immediately).

## Candidates (LLM-proposed, human-reviewed)

This section mirrors `loop-inventory.md.candidates` once the weekly `LoopDiscoveryTest` starts running. Items move from candidates to **Entries** above only after a human review confirms the loop is real and assigns a budget + test. Empty for now.
