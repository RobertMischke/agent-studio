# Task Server gate topology

Status: decided target architecture, not yet implemented, 2026-08-21. This
page documents the target shape for build/test (and later lint/analysis)
gates as first-class, claimable Task Server work items: `GateSubject`,
`GateAttempt`, `GateLease`, `GatePlan`, `GateReport`. The shape was
recommended and decision-ready in the
[Remote Gate Target Architecture Dossier](../../operations/remote-gate-zielbild/index.html)
(AGT-W18) and is already treated as the adopted target by
[`docs/system/domains/pipeline.md`](../../system/domains/pipeline.md) ("the
approved W18 Remote Gate target architecture") and
[`docs/operations/remote-task-server-local-studio.md`](../../operations/remote-task-server-local-studio.md)
(which sequences the migration against this exact contract as G0/G1/G2).

**Nothing here is shipped.** A repository check on 2026-08-21 found no
`GateSubject`, `GateAttempt`, or `GateLease` type in `backend/`. The only
existing types with similar names — `BuildTestGateSubject`
(`backend/Features/Runner/ReviewDecisionOrchestrator.cs`) and
`MachineGateLease` (`backend/Features/Pipeline/BuildTestGateRunner.cs`) —
belong to the *current* SSH-bridge implementation this page's target
replaces, not to the target contract. Do not confuse the two when reading
code.

## Why this exists

The current `post-build-test-gate` path (`BuildTestGateRunner.cs`) is fast but
out of band: `ReviewDecisionOrchestrator` derives an SSH alias from the
configured execution runner and shells out directly to a remote host, which
scans `$HOME/runner-work/*/repo` by glob, fetches, and reports only an exit
code. The only central visibility is `RemoteGateActivityStore`, which is
process-local, starts late, has hard-coded capacity, and is lost on backend
restart. Worse, once the remote command loop starts, a nonzero SSH exit can be
recorded as the gate *result* instead of routing through typed infrastructure
retry — the transport boundary leaks into product/infrastructure
classification. This is the same failure class the
[Distributed Agent Studio target architecture](../distributed-agent-studio-target-architecture.md)
names generally: the backend currently guesses host work instead of the Task
Server owning that authority.

The fix is not a new supervising actor on the remote host. It reuses the
Tranche 0 split already decided in
[distributed-agent-studio-target-architecture.md](../distributed-agent-studio-target-architecture.md):
the Task Server owns durable truth, the Orchestrator Engine owns dispatch and
policy, and an Agent Host executes. `GateAttempt` is that split applied
specifically to deterministic build/test/lint gates, kept separate from the
coding `RunAttempt` and the semantic/visual `ReviewAttempt` lifecycles.

## The moving parts (target contract)

| Object | Required facts | Owner | Invariant |
|---|---|---|---|
| `GateSubject` | subject ID, task/source run, repository ID and URL, expected SHA, result ref or source-bundle ID+digest, plan/policy hash, pipeline definition version, test-selection audit digest | Task Server | Immutable after creation; idempotent creation from source run + gate ID + plan hash |
| `GateAttempt` | attempt number, state, executor/host, failure classification, outcome, created/claimed/reported/cleaned timestamps | Task Server | At most one live fenced attempt per subject and gate policy |
| `GateLease` | lease ID, attempt ID, executor/instance/host, fence, authority epoch, acquired/expires, resource namespace | Task Server | Renew/report/cleanup must match executor, instance, lease, fence, and epoch |
| `GatePlan` | catalogue gate ID, typed commands/argv, working subdirectory, command and overall deadlines, required capabilities, output limits, cleanup policy | Engine creates; Task Server freezes | Versioned and bounded; the executor cannot add work |
| `GateReport` | outcome, typed classification, tested SHA and tree, dirty-before/after, command evidence, environment/toolchain identity, output/artifact digests, cleanup status | Agent Host reports; Task Server validates | Tested SHA must equal expected SHA; stale or duplicate-conflicting reports are rejected |

Claims use a dedicated `gate-step` kind, separate from coding and review
attempts, but reusing their lease/fence/idempotency conventions (see
[Fencing, leases, and authority](fencing-leases-and-authority.md) for the
generic lease/fence sequence this reuses — that page is not gate-specific and
this page does not duplicate its fencing mechanics). Admission is
capability-based: `executor:gate` plus repository, Git fetch, disk, and
required toolchain capabilities; capability health can drain only affected
gate claims without touching coding or review capacity.

### Central state machine

`queued -> claimed -> materializing -> running -> reporting -> cleaning ->`
one of `passed | product-failed | infra-retry | infra-failed | timed-out |
cancelled`.

Studio derives queue age, active host, attempt count, phase, deadline, outcome,
and evidence availability from these durable facts instead of the current
hard-coded, process-local capacity snapshot.

### Materialization sequence

1. The Engine resolves the authoritative result SHA and creates the immutable
   `GateSubject` through the Task API.
2. The Task Server admits a claim only when the host has fresh matching
   capabilities and an available gate slot.
3. The Agent Host fetches the declared result ref (or the declared,
   digest-pinned source bundle if unavailable) — never a glob scan of
   unrelated host repositories.
4. The host verifies repository identity and object type, creates a
   fence-specific disposable workspace, checks `HEAD == expectedSha`, and
   records tree/dirty proof.
5. The host executes the frozen plan under per-command and overall deadlines,
   records bounded evidence, cleans the workspace, and submits a fenced
   report.
6. The Task Server validates the report; the Engine consumes the terminal
   outcome and applies pipeline policy.

### Timeout and fallback taxonomy

| Trigger | Target classification |
|---|---|
| No executor before the dispatch deadline | `NoEligibleGateExecutor` — remains centrally queued; never falls back to a Task Server checkout |
| Heartbeat or host lost | Expire the attempt, raise the fence, retry the same immutable subject on an eligible host after positive no-overlap/containment evidence |
| Command/build/test deadline exceeded | Kill the process tree in the attempt namespace; classify as infrastructure unless the plan defines an explicit product timeout |
| Tests fail normally | `ProductFailure`, terminal — never retried on another host to "seek a green result" |
| Preferred host unavailable | Retry on another capable host, optionally a registered workstation Agent Host with the same contract |
| Retry budget spent with no trustworthy result | Terminal `GateInfra`, all attempts visible — never relabeled as a product/quality failure |

## Decided vs. open

Decided (already referenced as adopted by `pipeline.md` and
`remote-task-server-local-studio.md`):

- Primary shape is a dedicated claimable `GateAttempt` in the Task Server
  protocol, not gate-as-review-aspect and not capability-only dispatch.
- `executor:gate` plus toolchain capabilities gate *admission* into a claim;
  capability alone is never a work item.
- Fallback is another eligible Agent Host (optionally a registered
  workstation); exhausting the retry budget terminalizes as `GateInfra`, never
  a silent in-process Task Server fallback.
- Reuse boundary is the Review Workspace *primitives* (materialization,
  containment namespace, command evidence, environment proof, cleanup) —
  not the `ReviewAttempt` lifecycle itself.
- Initial scope is the bounded gate-step core migrating `post-build-test-gate`
  first, then `post-lint-scss`; no generic arbitrary post-step runner.

Still open / not yet true:

- No implementation card exists yet with a real task key — the dossier's
  `implementationTasks` entry ("Move build/test gates to claimable Task
  Server gate steps") is a proposed card only.
- Whether the local in-process compatibility fallback is removed or explicitly
  retained is an unmade decision; it must not survive by accident as a silent
  target fallback.
- The AGT-2262 SSH-bridge teardown stays parked until a default-on cutover
  plus a restart/failure canary (remote pass, product failure, host loss, Task
  Server restart, Engine restart, no eligible host, cleanup failure) proves
  parity — none of that evidence exists yet.
- Today's interim state (per `docs/system/domains/pipeline.md`) already routes
  the review build/test gate through the *existing* `ReviewAttempt` adapter for
  Remote parity; that is explicitly narrower than, and not a substitute for,
  this target's dedicated `GateAttempt`.

## Source dossier and follow-up chain

Full rationale, the three compared shapes (claimable step vs. Review Plane vs.
capability-only dispatch), and the trade-off table live in the
[Remote Gate Target Architecture Dossier](../../operations/remote-gate-zielbild/index.html)
(AGT-W18, source task AGT-2369). Its follow-up chain: sight-review decision
(done, decision-ready) -> one new implementation card (not yet created) ->
AGT-2262 SSH-bridge teardown (existing, blocked on cutover acceptance).

## Living knowledge log

Append new findings about the Task Server gate topology here, newest on top.
Keep each entry short: date, what was learned, and a pointer to the
code/commit/task.

- **2026-08-21.** Documentation-transfer extraction pass from the AGT-W18
  Remote Gate Target Architecture dossier. Confirmed by code search that no
  `GateSubject`/`GateAttempt`/`GateLease` type exists in `backend/` yet — only
  the unrelated legacy `BuildTestGateSubject` / `MachineGateLease` types used
  by the current SSH-bridge path. Confirmed the target shape is nonetheless
  treated as decided/adopted by `docs/system/domains/pipeline.md` and
  `docs/operations/remote-task-server-local-studio.md`, which already sequence
  their own migration plans against it.
