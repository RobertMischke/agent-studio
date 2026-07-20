# Runner Domain Map

Version: 2026-07-13
Status: System-of-record map for runner-side changes.

Use this when a change touches task pickup, active execution, post-run outcome
policy, reissue behavior, crash recovery, supervisor loops, or runtime runner
state.

## Entry Points

- Start with [docs/common-problems/](../common-problems) for recurring
  runner, CLI, permission, filesystem, and state-machine failures.
- Use [Services killed by a harness sweep](../../operations/common-problems/services-killed-by-harness-sweep/)
  when choosing whether a command is session-owned, OS-owned, or a child
  process that must remain owned by one coding run.
- Use [docs/system/contracts/run-outcome.md](../contracts/run-outcome.md) for the shared
  classification that drives lane routing, `status.md`, and frontend failure
  surfacing.
- Use [Runner provenance, host handoff, and continuation](../../concepts/completion-review-and-remote-runner-stability.html#provenance)
  when a change touches historical runner attribution, runner assignment during
  active work, cross-host continuation, attempt identity, or per-step placement.
  It defines the deferred-switch default, the no-hot-migration boundary, and the
  ordered runner route shown in Overview and task detail.
- Read [docs/concepts/orchestrator-drive-to-conclusion.html](../../concepts/orchestrator-drive-to-conclusion.html)
  before touching reissue, retry, CLI-crash, or classifier logic: it holds the
  target model (retry-with-cooldown, no classifier-unknown, honest human-review
  terminal) and a running case log. Append new crash incidents there.
- Use [docs/system/contracts/agent-task.md](../contracts/agent-task.md) for the boundary
  between the application-owned lifecycle and the CLI-owned task work.
- Use [docs/research/orchestrator-decision-protocol-2026-05.md](../research/orchestrator-decision-protocol-2026-05.md)
  and [docs/system/architecture/decisions/adr-archive.md](../architecture/decisions/adr-archive.md) for ADR-0002
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
- `backend/Features/Orchestrator/OrchestratorContextDigestService.cs` and
  `OrchestratorContextEndpoints.cs`: ORCH-1 read context shared by side-sheet
  chat turns and session turns. The bounded digest folds board transitions,
  active lifecycle phases, cached quota, PUB-1 targets, backend/watcher health,
  and decision-journal excerpts according to the `global|project|task` key.
  `POST .../refresh` is the explicit expensive path that re-probes quota first.
- `backend/Features/Registry/OrchestratorSettingsResolver.cs`: pure two-tier
  resolver for the workspace-shaped orchestrator knobs (model, thinking level,
  autonomy) - `project override → workspace default → platform constant` - plus
  the injectable `OrchestratorDefaultsProvider` the read sites call (ADR-0061).
  The autonomy read sites are `ProjectRunner` (orchestrator model override) and
  `OrchestratorPrepHostedService` / `IntakeHostedService` (autonomy level).
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
- `runner/*`: the standalone remote runner daemon. A dependency-free console
  process that continuously claims server-assigned projects with bounded host
  slots (default 2), fenced leases + heartbeat, per-task linked git worktrees,
  log/artifact upload, and fenced normal completion into auto-review. The original `--task <key>`
  one-shot remains for diagnostics. It owns no task state. Its only git write to
  origin is the mandatory teardown salvage branch described below.
  Operator runbook:
  [docs/operations/setup/linux-runner-host.md](../../operations/setup/linux-runner-host.md).
- `AttemptAuthorityService` + `RunLeaseService` + `AttemptAuthorityEndpoints`
  (AGT-2182): the Task Server's persisted control-plane authority for separate
  `RunAttempt`, `ReviewAttempt`, and immutable `ReviewSubject` records. The store
  owns stable attempt IDs, repository/task/source identity, leases, per-task
  monotonic fences, authority epoch, heartbeat, terminal facts, evidence digests,
  and idempotency. It lives under `<TaskRepository>/.metadata/` and performs no
  checkout, build, test, provider CLI, vision, or semantic review work.
  `AgentSession` and process-holder identity remain continuity metadata only;
  neither can mint or recover attempt write authority.
- `TaskRunnerService.ProjectRunnerBadge` + `TaskEndpointHelpers.WithRuntime`
  (AGT-2003, canonicalized by AGT-2182): read-time projection of the active
  persisted RunAttempt lease onto `TaskInfo.Runner`
  for `3-progress` cards, so the board can show which runner executes a card
  (remote `⇥ <runner>` from the lease owner vs a quiet `lokal` in-process run).
  The projection includes canonical Attempt ID and authority epoch alongside
  the lease and fence. A remote runner acquires the run lease; the local
  in-process runner uses the disk pickup-lock and holds none, which is exactly
  the lokal-vs-remote signal.

## Invariants

- Coding-slot occupancy follows live CLI processes, not lane membership. A
  `3-progress` card in `loop-waiting`, `steer-pending`, or post-processing keeps
  no execution seat; a continuation must pass admission again and remains
  visibly queued when no seat is free. A heartbeat-less `3-progress` card may
  survive the liveness grace only with one of the explicit waiting phases.

- Remote pickup ownership lives in the project record (`executionRunner` plus
  `remoteExecutionEnabled`). The remote claim endpoint and local ProjectRunner
  consult the same record; assigned remote-capable projects are never locally
  auto-picked. Lease fencing is the hard split-brain guard below that policy.

- Sentinel matches are authoritative. When adding a sentinel, update
  [docs/system/contracts/agent-task.md](../contracts/agent-task.md) and
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
  [docs/system/contracts/run-outcome.md](../contracts/run-outcome.md).
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
- Host-load admission (AGT-2077) samples total system CPU every 15 seconds. A
  continuous minute above 90 percent activates `load-throttle`: existing runs
  continue, new slot picks are deferred with timeline and orchestrator-feed
  decisions, while support OneShots and build/test post-gates queue until
  cooling. Build/test post-gates are also serialized per Git repository before
  they enter host-load admission. Calls released after a load phase receive a
  3x timeout and one timeout retry after cooling. These failures are
  `environmental-load`, never a work-quality conclusion.
- No-progress failures count across auto-pickup and `UserContinue` reissues
  until progress, review, or quarantine resets the streak.
- Orchestrator session turns use the existing CLI session machinery. A context
  with a stored session id resumes that session; otherwise the first turn starts
  a fresh run and persists the captured session id. Active turns are capped by
  `Orchestrator:SessionTurns:ActiveLimit` with default `4`; overflow responses
  return `status: "queued"` and a one-based `queuePosition`. Posting `/park`
  cancels the active turn for that context and parks queued turns for the same
  context.
- Every orchestrator chat entry point receives the same ORCH-1 application
  digest. Global context may read all registered projects; project and task
  contexts are project-isolated, with task context adding only its focused task.
  Digest sections are capped and omit raw quota samples and full decision
  prompts/responses. Normal turns use cached quota; only the explicit refresh
  endpoint starts quota probes.
- Every coding run is worktree-isolated - single-slot resume/reissue included,
  not just parallel slots. The shared main checkout is read-only reference + the
  integration target; on a failed worktree prepare the run is deferred, never
  run in the main checkout, and a coding run that resolves to the main checkout
  is refused + escalated. Read-only (planning / research) and epic-planning runs
  run in-place. See
  [ADR-0057](../architecture/decisions/adr-archive.md#adr-0057---always-worktree-garantie-every-coding-run-is-worktree-isolated-including-single-slot-resumereissue-with-a-main-checkout-guard-2026-06-22).
- Steer-timeout (Run-Liveness Slice B): an auto-mode run that asks a steer /
  `[[TASK_NEEDS_INPUT]]` question the orchestrator cannot answer leaves a durable
  `steer-pending.json` marker + a visible `steer-pending` phase, and a bounded
  sweep (`SteerTimeoutMonitor` over `SteerTimeoutPolicy.Decide`) resolves the wait
  after `Runner:SteerTimeout:TimeoutSeconds` (default 120s): auto-answer from the
  branch state when the question is unambiguous, else a `steer-timeout` blocked
  escalation. A steered card never waits indefinitely. See
  [docs/concepts/run-liveness-and-slot-semantics.md](../../concepts/run-liveness-and-slot-semantics.md).

- Supervisor code is advice-first. Emergency primitives must call runner
  services, not poke task state directly.
- Teardown never drops uncommitted work. `WorktreeTaskLifecycle.TeardownIfIntegrated`
  snapshots any dirty/untracked worktree onto its `task/<id>` branch as a
  platform WIP safety commit before removing anything, and refuses teardown if
  that snapshot fails (AGT-1945). The merged-ancestor gate alone is not enough:
  a failed auto-commit leaves the branch tip at develop, which reads as "merged"
  and would force-remove the deliverable. Genuine auto-commit failures at
  integration are surfaced as a High `integration-error`, never silent.
- Remote teardown is also fail-closed. `runner/GitWorkspace` checks status on
  every normal, exceptional, shutdown, and crash-debris teardown. It commits a
  dirty checkout as `wip(runner): salvage before teardown - outcome <X>` and
  pushes run-produced commits to `runner/<runner-id>/<task-key>`. The remote ref
  is verified before removal. A failed check or push keeps the worktree and
  records a `worktree-blocked` gate item with host and path. Successful salvage
  branches are linked from the card's `results/deliverables.md`.
- Remote daemon admission is write-capability gated. Startup keeps the fetch URL
  and Git `pushurl` separate, performs one push dry-run, and publishes the result
  on its client identity. A reported `read-only` identity receives no claims;
  Remote Hosts surfaces the same state for operator repair.
- Workspace-shaped orchestrator settings (model, thinking level, autonomy)
  resolve `project override → workspace default → platform constant` through
  `OrchestratorSettingsResolver`, never read ad-hoc at a call site. The provider
  is tolerant: an unmapped project or an empty workspace tier collapses to the
  old project-only chain, so an empty workspace-settings store is byte-for-byte
  identical to pre-migration behaviour. The process-wide supervisor/orchestrator
  lifecycle flags stay platform-global in `OrchestratorConfigService` and are
  **not** workspace-shaped. See
  [ADR-0061](../architecture/decisions/adr-archive.md#adr-0061---orchestrator-settings-are-a-two-tier-config-project-override-wins-over-workspace-default-wins-over-platform-constant-2026-07-11).

## Verification

- Outcome and grammar changes need focused unit tests for analyzer, policy,
  reply parser, quarantine, and escalation paths.
- Pickup, dead-letter, active-run, and recovery changes need integration-style
  tests around `ProjectRunner` or the service that owns the transition.
- Prompt-template changes under `prompts/runtime/` require the matching live CLI
  probe, not only rendered-string tests.
