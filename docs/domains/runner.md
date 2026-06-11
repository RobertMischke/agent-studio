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
- `backend/Services/Runner/AgentOutcomeAnalyzer.cs`: terminal sentinel and
  issue-kind classification.
- `backend/Services/Runner/RunOutcomePolicy.cs`: deterministic outcome action
  mapping.
- `backend/Services/Runner/OrchestratorChatLog.cs`: typed orchestrator messages
  written into `logs/cli-output.log`.
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

## Invariants

- Sentinel matches are authoritative. When adding a sentinel, update
  [docs/contracts/agent-task.md](../contracts/agent-task.md) and
  `AgentOutcomeAnalyzer.SentinelRegex`.
- The agent classifies its run. The rule engine decides reissue, stop,
  escalation, and lane movement.
- A re-open starts a new run. It must rerun pre steps, core, post steps, and
  append run history instead of flattening earlier evidence.
- Context overflow is non-retryable and routes to human review on first
  detection.
- No-progress failures count across auto-pickup and `UserContinue` reissues
  until progress, review, or quarantine resets the streak.
- Supervisor code is advice-first. Emergency primitives must call runner
  services, not poke task state directly.

## Verification

- Outcome and grammar changes need focused unit tests for analyzer, policy,
  reply parser, quarantine, and escalation paths.
- Pickup, dead-letter, active-run, and recovery changes need integration-style
  tests around `ProjectRunner` or the service that owns the transition.
- Prompt-template changes under `prompts/runtime/` require the matching live CLI
  probe, not only rendered-string tests.
