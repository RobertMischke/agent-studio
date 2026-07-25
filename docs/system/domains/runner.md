# Runner Domain Map

Version: 2026-07-23
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
  the cwd-keyed session-resume gate (see ADR-0057). Every source-mutating run,
  including a single-slot run, requires an authoritative Git repository and its
  own task worktree. A non-Git project is rejected for mutating runs instead of
  falling back to in-place execution; read-only planning and research remain
  eligible to run in place.
- `backend/Services/Runner/AgentOutcomeAnalyzer.cs`: terminal sentinel and
  issue-kind classification.
- `backend/Services/Runner/RunOutcomePolicy.cs`: deterministic outcome action
  mapping.
- `backend/Services/Runner/OrchestratorChatLog.cs`: typed orchestrator messages
  written into `logs/cli-output.log`.
- `backend/Features/Runner/OrchestratorChat.cs` and `OrchestratorRunner.cs`:
  side-sheet chat dispatch. This operating mode accepts the effective model and
  reasoning choice from the live Codex catalogue, executes through the Codex
  one-shot registry, and rejects non-GPT models without a Claude fallback.
- `backend/Features/Runner/RemoteChatWorkBroker.cs`,
  `backend/Features/Tasks/LeaseEndpoints.cs`, and
  `runner/RemoteProjectChatRunner.cs`: assignment-aware remote side-sheet chat
  dispatch. The Runner claims and renews opaque chat work, prepares the
  project's dedicated chat checkout from its normal git cache, starts Codex
  there, and completes with the observed hostname, repository path, branch,
  and HEAD revision.
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
- `runner/*`: the standalone `agent-host` daemon. A dependency-free console
  process that runs as either a separately registered `coding` or `review`
  service. Coding continuously claims server-assigned projects with bounded
  host slots (default 2), fenced leases + heartbeat, per-task linked git
  worktrees, log/artifact upload, and fenced normal completion into auto-review.
  Review claims one immutable ReviewSubject, creates a fresh disposable
  exact-SHA workspace, runs the server-supplied existing aspect command plan,
  and sends one fenced evidence report plus cleanup proof. The original `--task <key>`
  one-shot remains for diagnostics. It owns no task state. Its only git write to
  origin is the mandatory teardown salvage branch described below.
  Operator runbook:
  [docs/operations/setup/linux-runner-host.md](../../operations/setup/linux-runner-host.md).
- `AttemptAuthorityService` + `RunLeaseService` + `AttemptAuthorityEndpoints`
  (AGT-2182): the Task Server's persisted control-plane authority for separate
  `RunAttempt`, `ReviewAttempt`, and immutable `ReviewSubject` records. The store
  owns stable attempt IDs, repository/task/source identity, leases, per-task
  monotonic fences, authority epoch, heartbeat, terminal facts, evidence digests,
  and task-and-operation-scoped idempotency. Remote completion carries an
  explicit immutable Result-SHA independently of optional salvage-branch
  metadata, and rejected late review reports remain non-authoritative attempt
  history. Only an exact
  acquire-delivery replay is idempotent; a new acquire from the same executor
  cannot renew a live lease without the canonical attempt and fence. Replayed
  review settlements become superseded once another subject is current. It
  lives under `<TaskRepository>/.metadata/` and performs no
  checkout, build, test, provider CLI, vision, or semantic review work.
  Remote claim and completion lane facts carry the same attempt, fence, epoch,
  and idempotency tuple. Claim, standalone acquire, and completion are serialized
  at the Task Server mutation boundary, and canonical Remote completion suppresses
  the generic local auto-commit, commit-attribution, drift, provenance, and
  post-processing queue path.
  `AgentSession` and process-holder identity remain continuity metadata only;
  neither can mint or recover attempt write authority. Failed authority-store
  persistence restores the last durable snapshot before the error escapes, so
  the live process cannot retain a fence, epoch, or attempt that restart would
  forget.
- `orchestrator-engine/`: the separate API-only flow executor. Its bounded
  ReviewDecision, Council, PostProcessing, GateDispatch, and CompletionJudge
  loops claim server-owned orchestration runs through
  `/api/v1/orchestration/*`. It references only the shared Task Server
  contracts and has no TaskScanner, task-folder, or store access.
- `task-server/TaskServerOrchestrationStore.cs`: durable flow definitions,
  orchestration runs, stage results, leases, fences, and restart recovery.
  Expired Engine leases return the same run to `pending`; a replacement Engine
  receives a higher fence and stale settlement is rejected.
- `backend/Features/Runner/OrchestrationExecutionMode.cs`: transition switch
  for the legacy host. `Orchestration:ExecutionMode` accepts exactly
  `Monolith` or `Engine`; Engine mode omits the legacy review/post-processing
  hosted services from the monolith.
- Canonical Remote ReviewAttempts are excluded from the legacy
  `ReviewDecisionOrchestrator` scan. They remain visibly in Auto Review until a
  fenced Remote Review Executor claims them. This is the fail-closed bootstrap
  boundary: the Task Server never substitutes its checkout or local
  `session-events.jsonl` for the ReviewSubject. Legacy tasks without attempt
  authority continue through the established local compatibility path.
- `task-server.Tests/TopologyTests.cs`: release-blocking sibling-process
  harness for Studio detach, canonical history replay, renewal safety stop,
  Task Server restart quarantine, Runner replacement after positive
  no-overlap proof, and authenticated HTTPS event transport.
- `TaskRunnerService.ProjectRunnerBadge` + `TaskEndpointHelpers.WithRuntime`
  (AGT-2003, canonicalized by AGT-2182): read-time projection of the active
  persisted RunAttempt lease onto `TaskInfo.Runner`
  for `3-progress` cards, so the board can show which host executes a card
  (remote `Host · <runner-id>` from the lease owner vs a quiet `Local`
  in-process run).
  The projection includes canonical Attempt ID and authority epoch alongside
  the lease and fence. A remote runner acquires the run lease; the local
  in-process runner uses the disk pickup-lock and holds none, which is exactly
  the lokal-vs-remote signal.

## Invariants

- Coding and review service identities are not interchangeable. A
  `review-executor` capability cannot claim coding work, mixed capabilities are
  rejected, and a registered identity cannot switch executor roles. Review
  claim, renew, report, and cleanup use a separate lease and monotonically increasing fence.
  Same-host placement is allowed only with the distinct service identity,
  instance, workspace root, cache, port block, container/database namespace,
  read-only credentials, and quota. A ReviewPlan may require a different host
  failure domain.
- Every review command records the expected Result-SHA and the actual HEAD
  immediately before process start. The Task Server accepts evidence only when
  the repository identity, expected SHA, tested SHA, tree, executable-digest
  toolchain, output-artifact, and containment facts match the immutable subject.
  Missing source is `ReviewInfra` /
  `SnapshotUnavailable`; wrong repository, wrong SHA, dirty-before, and
  mutated-after are typed infrastructure outcomes. They stay in Auto Review and
  consume no coding attempt.
- Draining closes review admission but not active renew, report, or cleanup.
  Safe shutdown and restore include unresolved ReviewAttempt authority, and
  restart takeover changes both the durable fence and the containment namespace.
  A report is also rejected when its immutable subject no longer owns the task's
  Auto Review lifecycle.
- Capability-aware Remote admission (AGT-2186) is Task Server authority, not a
  daemon-local slot reduction. Coding and review services publish versioned,
  expiring health for provider authentication, Git fetch/push, repository
  access, .NET, Node, Playwright, vision, disk, Task Server connectivity, and
  platform/toolchain identity. Claims persist their required set. One
  capability advances through `healthy -> suspect -> draining -> half-open ->
  healthy` with bounded thresholds, exponential cooldown, and one fenced
  canary. Matching new claims stop while unrelated capabilities and the
  configured parallelism continue. Active leases are never revoked by this
  admission decision. Disk full, invalid lease authority, host network
  isolation, repository filesystem corruption, and Task Server authority
  uncertainty use the separate automatic whole-host drain. Operator drain is a
  distinct API, audit action, persisted field, and UI label. Capability failure
  reports must bind the active coding or review claim and fence; stale and
  duplicate deliveries fail closed or replay idempotently.

- Coding-slot occupancy follows live CLI processes, not lane membership. A
  `3-progress` card in `loop-waiting`, `steer-pending`, `quota-waiting`, or post-processing keeps
  no execution seat; a continuation must pass admission again and remains
  visibly queued when no seat is free. A heartbeat-less `3-progress` card may
  survive the liveness grace only with one of the explicit waiting phases.

- Remote host capacity is reported as distinct workload classes. RUN occupancy
  comes from every daemon claim poll (`ActiveSlots` plus `AvailableSlots`, whose
  sum is the configured host maximum). Remote SSH build/test GATE occupancy
  comes from gate start/completion events and runs outside RUN slots. Host CPU
  and load include both pools and unrelated processes, so neither is inferred
  from lane membership or from CPU percentage. This keeps claim/lane drift
  visible instead of silently folding it into a slot count.

- Remote pickup ownership lives in the project record (`executionRunner` plus
  `remoteExecutionEnabled`). The remote claim endpoint and local ProjectRunner
  consult the same record; assigned remote-capable projects are never locally
  auto-picked. Lease fencing is the hard split-brain guard below that policy.

- Side-sheet project and task chat follows the same remote pickup ownership.
  A remote-assigned project's chat work is claimable only by its assigned
  Runner and executes inside a host checkout from the same project git cache as
  card runs. A project without a remote assignment executes chat locally. Each
  response projects the actual local or remote hostname, repository path,
  branch, and HEAD; a reassignment invalidates a cached host context.
- A planned `agent-host` daemon restart is an execution handoff, not an attempt
  boundary. The daemon persists lease, fence, Task Server run/instance,
  worktree, detached-worker PID/start time, and file-log progress below
  `RUNNER_STATE_DIR`. SIGTERM stops claims and exits without cancelling those
  workers. A pre-launch slot marker plus worker-written atomic identity closes
  the `Process.Start`-to-slot-save handoff window. The replacement renews
  authority only after PID-generation and Linux `/proc/<pid>/cwd` match the
  persisted worktree, then follows JSONL output and completes the same attempt.
  Missing or mismatched processes are actively released and returned to Ready;
  DB lease presence alone is never process-liveness evidence. systemd must use
  `KillMode=process`.
- A failed lease renewal consumes the last server-issued authority window.
  The standalone Runner stops before the known expiry minus one renewal
  interval, cancels the CLI process tree, and does not turn transport loss into
  autonomous authority. Task Server restart records `process-unknown`; only
  positive containment or infrastructure-fencing proof permits a higher-fence
  replacement.
- A remote project clone is eligible only when the project registry contains a
  repository URL. On every new clone and refresh, the standalone runner sets
  both fetch and push URLs to that registry value and logs the effective pair.
  Host-level probe and one-shot fallback URLs never flow into project clones.
  A project without a registry URL stays Ready, is reported as not
  remote-capable, and creates no clone.
- Before the first card for one host/project pair is leased, the claim endpoint
  returns an unleased preflight offer containing the registered repository and
  a registration fingerprint. The host creates or refreshes the exact shared
  project clone, verifies that both `origin` fetch and push URLs equal the
  registration, fetches, and proves write access by creating and removing a
  temporary runner ref. It reports the result in the next claim request. Only a
  matching green result may cross `2-ready` to
  `3-progress`. The server persists the result on the host identity and reuses
  it for following cards without another offer roundtrip. Repository
  registration and integration-branch mutations invalidate every host's cached
  result for that project. A failure remains visible on the Remote Hosts card
  and project execution card.

- A fresh `2-ready` Epic is remotely claimable as an Epic planning run. It
  occupies a normal host slot and holds the same fenced lease, heartbeat,
  cancellation, drain, and telemetry contract as a coding task, but it is not a
  coding work item. The server renders `epic-decomposition.md`, and the remote
  host runs it in a detached disposable checkout. No task branch, salvage
  commit, or push is created. Only the children produced by the plan enter the
  coding pipeline. An interrupted assigned card whose lease is free is requeued
  to Ready inside the next atomic claim before a higher fence is issued.

- A terminal sentinel in the final agent reply is authoritative. Sentinel-shaped
  text in streamed tool output, diffs, file content, or stderr is not a verdict.
  When adding a sentinel, update
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
  (`RunQuarantineBreaker.CountsAsNoProgressFailure`). The shared reissue budget
  belongs to a review-attempt epoch. Only an explicit human move out of
  `5-human-review` / `5e-escalated` opens the next epoch and rotates stale
  verdict artefacts; automatic verdicts and moves retain the current epoch and
  cannot replenish the ceiling. `OperatorReviewRequeueService` owns the epoch
  boundary, history rotation, decision-journal row, and timeline event.
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
- Side-sheet Orchestrator chat is GPT-only. Its selected model and reasoning
  level travel on every Board or Task context request. The backend may resolve
  an omitted model to the detected Codex default, but it must never route this
  mode to Claude.
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
- Remote teardown is also fail-closed. Protocol 2 coding runs journal logs,
  status, artifacts, Git facts, terminal facts, the immutable result envelope,
  and server acknowledgements under
  `$RUNNER_WORKDIR/outbox/<run-attempt-id>/`. `runner/GitWorkspace` first commits
  dirty work, preserves the moving salvage ref without force push, and publishes
  the exact result to
  `refs/heads/agent-studio/results/<run-attempt-id>/<result-sha>`. Cleanup then requires
  the Task Server acknowledgement for the matching canonical envelope digest.
  A process restart replays the original outbox before new claims and never
  starts the coding CLI. Transfer failure stays `transfer-recovery`, retains the
  worktree, and consumes no coding or completion budget.
- The Task Server stores one result envelope per RunAttempt with repository ID
  and URL, base and result SHA, immutable ref or source-bundle digest,
  artifact-manifest digest, and applicable submodule and LFS identities. Handoff and completion
  have idempotency keys plus monotonic host sequence numbers. A response lost
  after commit therefore returns the original acknowledgement and cannot repeat
  a lane transition. Protocol 1 cannot call the protocol 2 handoff or completion
  path.
- Result refs and manifests have an earliest deletion time of 30 days by
  default. Reaching Completed or Archive extends that time to at least 30 days
  after the terminal transition. The current store performs no automatic
  deletion, so retention cannot end early.
- Retained remote-runner worktree pickup reconciles the local and canonical
  salvage tips by ancestry before reuse. Equal and remote-ahead tips keep the
  canonical remote ref, and local-ahead tips advance it with a normal
  fast-forward push. Divergent tips never rewrite that ref: the runner publishes
  the local tip to
  `runner/<runner-id>/<task-key>-collision-<local-sha>-<remote-sha>`, verifies
  both exact SHAs, and recreates the checkout from the explicit canonical SHA so
  the CLI can start. The collision ref and both tips are recorded in typed runner
  completion and operator deliverables. Publishing retries at most three times;
  an exhausted or genuinely unrecoverable git failure retains the worktree and
  uses the existing `worktree-blocked` escalation with the preserved tips and
  next safe action (AGT-2177).
- Epic planning is the deliberate exception: its detached checkout is checked
  for mutations and discarded without salvage. Any mutation invalidates the
  plan and returns the Epic to Backlog because planning is source-read-only
  (AGT-2178).
- Remote `agent-host` admission is write-capability gated. Startup keeps the fetch URL
  and Git `pushurl` separate, performs one push dry-run, and publishes the result
  on its client identity. A reported `read-only` identity receives no coding
  claims. The per-project delivery preflight is stricter and applies before any
  first project claim, including Epic planning: it creates and removes a
  temporary runner ref so server-side write policy is exercised. Remote Hosts
  surfaces both states for operator repair.
- Workspace-shaped orchestrator settings (model, thinking level, autonomy)
  resolve `project override → workspace default → platform constant` through
  `OrchestratorSettingsResolver`, never read ad-hoc at a call site. The provider
  is tolerant: an unmapped project or an empty workspace tier collapses to the
  old project-only chain, so an empty workspace-settings store is byte-for-byte
  identical to pre-migration behaviour. The process-wide supervisor/orchestrator
  lifecycle flags stay platform-global in `OrchestratorConfigService` and are
  **not** workspace-shaped. See
  [ADR-0061](../architecture/decisions/adr-archive.md#adr-0061---orchestrator-settings-are-a-two-tier-config-project-override-wins-over-workspace-default-wins-over-platform-constant-2026-07-11).

## Live execution ownership projection

Every task read returns `executionLocation`, the canonical view of where a run
actually executes. A live local CLI process or a fenced run lease is the source
of truth. `ProjectSettings.executionRunner` is returned separately as
`configuredRunnerId` and explains routing only; it never replaces the actual
owner. This makes configured-vs-actual mismatches explicit during recovery or
operator intervention.

The projection distinguishes `local-running`, `remote-running`,
`remote-disconnected`, `queued-remote`, `recovering`, and
`no-active-execution`. Remote leases retain their last accepted heartbeat, so a
missed heartbeat becomes acute only after the stale window. A renewed lease
returns to healthy without a page reload through the normal task push/poll path.
Run-start session events capture the execution projection so finished run
history keeps its stable runner-id attribution with `historical: true` and renders
quietly. The wire contract is
[task-execution-location.schema.json](../schemas/task-execution-location.schema.json).

## Verification

- Outcome and grammar changes need focused unit tests for analyzer, policy,
  reply parser, quarantine, and escalation paths.
- Pickup, dead-letter, active-run, and recovery changes need integration-style
  tests around `ProjectRunner` or the service that owns the transition.
- Prompt-template changes under `prompts/runtime/` require the matching live CLI
  probe, not only rendered-string tests.
