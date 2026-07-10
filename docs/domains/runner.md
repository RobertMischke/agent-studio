# Runner Domain Map

Version: 2026-06-09
Status: System-of-record map for runner-side changes.

Use this when a change touches task pickup, active execution, post-run outcome
policy, reissue behavior, crash recovery, supervisor loops, or runtime runner
state.

## Entry Points

- Start with [docs/wiki/common-problems/](../wiki/common-problems) for recurring
  runner, CLI, permission, filesystem, and state-machine failures.
- Use [docs/contracts/run-outcome.md](../contracts/run-outcome.md) for the shared
  classification that drives lane routing, `status.md`, and frontend failure
  surfacing.
- Read [docs/wiki/concepts/orchestrator-drive-to-conclusion.html](../wiki/concepts/orchestrator-drive-to-conclusion.html)
  before touching reissue, retry, CLI-crash, or classifier logic: it holds the
  target model (retry-with-cooldown, no classifier-unknown, honest human-review
  terminal) and a running case log. Append new crash incidents there.
- Use [docs/contracts/agent-task.md](../contracts/agent-task.md) for the boundary
  between the application-owned lifecycle and the CLI-owned task work.
- Use [docs/research/orchestrator-decision-protocol-2026-05.md](../research/orchestrator-decision-protocol-2026-05.md)
  and [docs/architecture/decisions/adr-archive.md](../architecture/decisions/adr-archive.md) for ADR-0002
  and runner decision rationale.

## Key Code

- `backend/Services/TaskRunnerService.cs`: project runner ownership and public
  start, stop, continue, and mode surface.
- `backend/Services/Runner/ProjectRunner.cs`: per-project pickup tick, active
  job latch, progress-first resume, dead-letter handling, and CLI spawn path.
- `backend/Features/Runner/WorktreeRunPolicy.cs`: pure always-worktree policy -
  whether a run must be worktree-isolated, the main-checkout guard condition, and
  the cwd-keyed session-resume gate (see ADR-0057).
- `backend/Services/Runner/AgentOutcomeAnalyzer.cs`: terminal sentinel and
  issue-kind classification.
- `backend/Services/Runner/RunOutcomePolicy.cs`: deterministic outcome action
  mapping.
- `backend/Services/Runner/OrchestratorChatLog.cs`: typed orchestrator messages
  written into `logs/cli-output.log`.
- `backend/Features/Orchestrator/OrchestratorContextKey.cs`,
  `OrchestratorSessionRegistry.cs`, `OrchestratorSessionEndpoints.cs`, and
  `OrchestratorTurnService.cs`: context-keyed global, project, and task
  orchestrator sessions, the `/api/orchestrator/sessions/{contextKey}/turns`
  and `/park` API surface, and session turn dispatch through the existing
  orchestrator CLI runner. Records persist under
  `<TaskRepository>/.metadata/orchestrator-sessions/<encoded>/`.
- `backend/Services/Runner/OrchestratorReplyParser.cs`: `{REPLY | STEER |
  BLOCK}` grammar.
- `backend/Services/Runner/RunQuarantineBreaker.cs`: no-progress failure streak
  circuit breaker.
- `backend/Services/Runner/EvidenceGate.cs`: visual and acceptance evidence
  gates before auto-accept.
- `backend/Services/Runner/CrashRecoveryService.cs`: recovery for orphaned run
  state after process failure.
- `backend/Services/Supervisor/*`: Layer 2 advisory loop, meta-cycle, and rare
  intervention primitives.
- `runner/*`: the standalone remote runner (RM-5, Runner-Split C). A dependency-
  free console process that runs one task on a Linux host against the Task Server
  API only (fenced lease + heartbeat, git-origin checkout, log + artifact upload,
  external-completion). Owns no task state and never pushes git. Operator runbook:
  [docs/operations/setup/linux-runner-host.md](../operations/setup/linux-runner-host.md).

## Invariants

- Sentinel matches are authoritative. When adding a sentinel, update
  [docs/contracts/agent-task.md](../contracts/agent-task.md) and
  `AgentOutcomeAnalyzer.SentinelRegex`.
- The agent classifies its run. The rule engine decides reissue, stop,
  escalation, and lane movement.
- Waits-on gate (AGT-2029): the ready-lane pickup gate
  (`ProjectRunner.IsReadyPickupCandidate`) skips a `2-ready` card whose
  `references.dependsOn` targets have not all reached `6-completed`/`7-archive`.
  Fulfillment is resolved cross-project and archive-inclusive
  (`TaskReferenceIndex` built from `ScanAllJobsWithArchive()`, shared with the
  read-time card overlay via `WaitsOnEvaluator`). A skipped card falls out of the
  candidate list and the tick picks the next eligible card - blocking is visible
  (the card's waits-on chip), never a silent deadlock. A `dependsOn` cycle is a
  configuration error: it is reported once per card (`waits-on-cycle` warning)
  and skipped, never deadlocked.
- A re-open starts a new run. It must rerun pre steps, core, post steps, and
  append run history instead of flattening earlier evidence.
- Context overflow is non-retryable and routes to human review on first
  detection.
- Post-processing classifies every run that did not sign off cleanly into one of
  five outcome buckets (`success` / `code-defect` / `environmental` /
  `inconclusive-with-results` / `inconclusive-empty`,
  `PostProcessingOutcomeTaxonomy`), and the bucket - not the raw exit code -
  drives what happens next. Environmental faults are never the change's fault:
  a *transient* one (host file lock in the MSB302x copy-lock family, network
  glitch, or a CLI launch/resume failure) retries with exponential backoff before
  escalating; the retry budget is bounded per kind (`EnvironmentalTransient` 2,
  `CliLaunchFailed` one fresh-start), and every environmental member that does
  escalate is flagged `environmental` so a reviewer never reads an infra blip as
  a failed change. An inconclusive run with files in `results/` routes to human
  review with a "partial work to inspect" hint rather than a bare `5e` park. See
  the taxonomy section of
  [docs/contracts/run-outcome.md](../contracts/run-outcome.md).
- Post-step (aspect / code-review) verdicts extend the same taxonomy: a missing /
  unparseable verdict caused by the reviewing CLI dying is ENVIRONMENTAL, not the
  card's work (AGT-2021, belege AGT-1996). The step reruns once with the
  environmental backoff (`PostProcessingOutcomeTaxonomy.DecidePostStepVerdictRetry`,
  `MaxPostStepVerdictRetries` = 1); a second miss records an `InfraCrash` flagged
  `environmental` and escalates via a chain-ending `Escalate` decision, so the
  card's reissue budget is never charged. See the pipeline domain map for the
  aspect-runner / orchestrator wiring.
- Environmental cycles do not count against progress or budget: a transient
  environmental fault never accrues toward the no-progress quarantine streak
  (`RunQuarantineBreaker.CountsAsNoProgressFailure`), and the shared reissue
  budget is counted per attempt chain, not over the job's whole lifetime -
  `ReviewDecisionOrchestrator.CountReissuesInCurrentChain` resets the count on the
  most recent chain-ending verdict (`Escalate` / `AcceptAsDone`) so a reopened
  card gets a fresh budget instead of escalating on the first new concern.
- No-progress failures count across auto-pickup and `UserContinue` reissues
  until progress, review, or quarantine resets the streak.
- Orchestrator session turns use the existing CLI session machinery. A context
  with a stored session id resumes that session; otherwise the first turn starts
  a fresh run and persists the captured session id. Active turns are capped by
  `Orchestrator:SessionTurns:ActiveLimit` with default `4`; overflow responses
  return `status: "queued"` and a one-based `queuePosition`. Posting `/park`
  cancels the active turn for that context and parks queued turns for the same
  context.
- Every coding run is worktree-isolated - single-slot resume/reissue included,
  not just parallel slots. The shared main checkout is read-only reference + the
  integration target; on a failed worktree prepare the run is deferred, never
  run in the main checkout, and a coding run that resolves to the main checkout
  is refused + escalated. Read-only (planning / research) and epic-planning runs
  run in-place. See
  [ADR-0057](../architecture/decisions/adr-archive.md#adr-0057---always-worktree-garantie-every-coding-run-is-worktree-isolated-including-single-slot-resumereissue-with-a-main-checkout-guard-2026-06-22).
- Supervisor code is advice-first. Emergency primitives must call runner
  services, not poke task state directly.
- Teardown never drops uncommitted work. `WorktreeTaskLifecycle.TeardownIfIntegrated`
  snapshots any dirty/untracked worktree onto its `task/<id>` branch as a
  platform WIP safety commit before removing anything, and refuses teardown if
  that snapshot fails (AGT-1945). The merged-ancestor gate alone is not enough:
  a failed auto-commit leaves the branch tip at develop, which reads as "merged"
  and would force-remove the deliverable. Genuine auto-commit failures at
  integration are surfaced as a High `integration-error`, never silent.

## Verification

- Outcome and grammar changes need focused unit tests for analyzer, policy,
  reply parser, quarantine, and escalation paths.
- Pickup, dead-letter, active-run, and recovery changes need integration-style
  tests around `ProjectRunner` or the service that owns the transition.
- Prompt-template changes under `prompts/runtime/` require the matching live CLI
  probe, not only rendered-string tests.
