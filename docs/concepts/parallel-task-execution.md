# Concept — Parallel Task Execution: worktrees, branch model (`develop`), git as pre/post pipeline steps, merge/PR

**Status.** Concept (sharpened design + slicing plan). This is the "full design" home that [ADR-0052](../architecture/decisions/adr-archive.md#adr-0052---intra-project-parallelism-is-now-an-opt-in-orchestrator-gated-capability-2026-05-31) points to. It is a design deliverable, not an implementation. Implementation is sliced in §8 and is gated on the crash-safe-pickup task.

**Source card.** `konzept-parallele-task-verarbeitung---worktrees-branch-modell-develop-git-als-prepost-agent-steps-mergepr` (ASS-589). Discussion with the operator on 2026-05-31.

**Date.** 2026-05-31.

---

## 0. What is already decided / already done (premise correction)

The source card frames the intra-project-parallelism non-goal as a *hard product boundary that this concept must reverse*, citing `IntakeRunner.cs:243` and `OrchestratorPrepRules.cs:107` as the enforcing guards. **That reversal already landed.** Recording it here so no slice re-does it:

- **The policy is already reversed.** [ADR-0052](../architecture/decisions/adr-archive.md#adr-0052---intra-project-parallelism-is-now-an-opt-in-orchestrator-gated-capability-2026-05-31) (Accepted, 2026-05-31) supersedes [ADR-0001](../architecture/decisions/adr-archive.md#adr-0001---sequential-per-project-parallel-across-projects-2026-04-15)'s "no intra-project parallelism, no worktrees, no branch-per-task". Bounded intra-project parallelism is now an opt-in, orchestrator-gated capability.
- **The intake/prep guards are already gone.** `IntakeRunner.CheckBlocked` now `return null;` (the "worktree"/"parallel coding"/"branch-per-task" phrase blockers were removed) and `OrchestratorPrepRules.HasOutOfScopeToken` now `return false;`. The cited line numbers no longer point at a guard — they point at the *removed-guard* comments referencing ADR-0052. **No slice should "remove the guard"; it is done.**
- **`maxParallelism` is referenced but not implemented.** It exists today only in comments/ADR text. It is not a field on `ProjectSettings`, not read by the runner, and the runner still enforces one active job per project (see §1).

So this concept is *purely additive* on an accepted decision. The remaining work is mechanism, not policy.

## 1. The foundation this docks onto (do NOT duplicate)

The product already has most of the scaffolding. Each item below is a docking point, with the exact code/ADR it lives in.

| Capability | Where it lives | How this concept uses it |
|---|---|---|
| **Pre/core/post pipeline** (first-class steps, per-job `pipeline-execution.json`) | [ADR-0045](../architecture/decisions/adr-archive.md#adr-0045---task-processing-as-a-first-class-pipeline-of-pre--core--and-post-steps-2026-05-29); `backend/Services/Pipeline/PipelineCatalogue.cs` (`StepKind` = Module/Core/Aspect/Orchestrator/Tool) | Git steps are new pipeline steps, not new infrastructure. Worktree-create is a **pre** step; commit/integration/cleanup are **post** steps. |
| **Per-project pipeline config** (reorder, enable, per-step model/mode) | ADR-0051 (proposed) + card `prepost-processing-steps-project-level-config…`; `ProjectSettings.PipelineSteps`, `ProjectSettingsService.SetPipelineStep`, `PipelineStepConfigResolver` | Home of the step definitions + the new `integrationStrategy` field. The git steps appear here as configurable steps. |
| **Deterministic commit attribution** (commit-to-task binding, `scripted` post-step) | [ADR-0050](../architecture/decisions/adr-archive.md#adr-0050---commit-attribution-regel-deterministic-commit-to-task-binding-2026-05-29); card `feature-post-step-git-commit-attribution-deterministic-commit-to-task-binding` | The Commit+Push agent step (§4) is the LLM-message sibling of this deterministic binding. They share attribution; do not fork it. |
| **Unified timeline ledger** (`timeline.jsonl`, `*_step_started/finished` kinds) | [ADR-0049](../architecture/decisions/adr-archive.md#adr-0049---self-contained-tasks-and-a-unified-timelinejsonl-ledger-2026-05-29) (ASS-560); card `architecture-self-contained-tasks--unified-timeline-view…` | Every git step + the pick decision render here (§6). No new event store. |
| **Completion loop** (retry-until-done, budgeted) | ASS-566; card `epic-orchestrator-completion-loop…` | An `orchestratorReaction: review` integration step that returns `reopen` ticks the loop budget. The loop *wraps* the pipeline (ADR-0051 §7). |
| **Git plumbing** | `backend/Services/GitService.cs` | Has `status`/`diff`/`Commit` (`add -A` + `commit -F -`)/`AutoCommitAsync`/`GenerateCommitMessageAsync` (Haiku) /`PushShaAsync`/commit-attribution helpers. Slice A/B added worktree add/remove, rebase, branch FF-merge, branch-parameterized push, retry, and best-effort remote cleanup for merged `task/<id>` branches. **Missing:** PR creation. |
| **Per-project serialization gate** | `ProjectRunner.cs:461` — `if (_processing || _activeJobId != null) return;` (comment at 472–474: "one coding CLI per project at a time") | This single `_activeJobId` latch is the thing `maxParallelism` generalizes into N slots. |
| **Pickup lease primitive** | `PickupLockFile` / `PickupLockOwner` (disk-backed `.pickup-lock.json`) | Single-machine guard only. Slice 2 can use one local lease per running task/slot, but multi-system execution needs the shared-store lease design in §8.2C. |
| **Crash-safe pickup** (PREREQUISITE) | card `bug-kritisch-crash-safe-pickup---eine-task-darf-niemals-den-backend-host-killen` | ADR-0052 gates this concept on it: N concurrent runs widen the crash surface. Must land first. |

## 2. Configuration (project level, `project-settings.json`)

New `ProjectSettings` fields (alongside `OrchestratorModel`, `AutoCommit`, `PipelineSteps`, …), all with a serial-preserving default:

| Field | Type | Default | Meaning |
|---|---|---|---|
| `maxParallelism` | int (1–N) | **1** | Concurrent worker slots for this project. `1` = today's serial behaviour, byte-for-byte. |
| `integrationBranch` | string | **`develop`**, with repository default fallback | Branch tasks branch from and integrate into. If the configured branch does not exist, the runner falls back to the repository default branch, for example `main` in a main-only repo. Per-user override keyed on `OwnerClientId` (see open decision D3). |
| `integrationStrategy` | enum | `direct-merge` | `direct-merge` \| `pull-request` (§5). |

"Slot / worker / lane" is a **runner** concept. Git only knows repo / branch / commit / worktree. The runner owns N slots; each occupied slot maps to one worktree + one `task/<id>` branch.

## 3. Branch model + isolation

- **Integration line = resolved `integrationBranch`** (configured branch when present, otherwise repository default branch). In the original default this is `develop`; main-only repositories resolve to `main`.
- **Serial (`maxParallelism = 1`):** work proceeds exactly as today. *Optionally* directly on `integrationBranch`, no task branch. Zero new worktree I/O when a project never opts in — this keeps ADR-0001's original "don't triplicate I/O for sequential work" objection answered.
- **Parallel (`maxParallelism ≥ 2`):** per concurrently-running task, an **ephemeral** branch `task/<id>` cut fresh from `integrationBranch`, checked out in its **own `git worktree`** (shared `.git` object store, no full clone). The branch lives for exactly one run and is deleted on cleanup. This is throwaway plumbing, **not** a feature-branch workflow.
- **Why worktree ⇒ branch-per-task:** git forbids two worktrees on the same branch, and two agents must never write the same working tree or race the same ref. Worktree-per-task therefore forces branch-per-task. Short branches keep merges small and conflict-rare.

## 4. Git belongs to the PIPELINE, not the agent

The **core run agent only edits files** in its worktree. It runs **no git**. Branch / commit / merge / PR / cleanup are pipeline steps (ADR-0045 `StepKind`).

```
PRE-STEPS (system):                 CORE RUN (agent):        POST-STEPS:
• worktree + task/<id> off develop   • edits ONLY files       • Commit+Push agent  (LLM step, §4.1)
• gather context                       in its worktree        • verify / lint / build gate (ADR-0051)
• exclusive/scope prep (§5, once)    • NO git                 • Integration agent  (LLM step, §4.2 / §5)
• inject prompt contract                                      • worktree + branch teardown
```

**Prompt contract** (a pre-step renders this into the core run prompt):

> Your workspace: branch `task/<id>`, worktree `<path>`. Work only here. **Do NOT commit, branch, or merge yourself** — the pipeline does that after you finish. Make only your file changes and end with the sentinel.

### 4.1 Commit+Push agent (LLM step)

Receives the worktree diff + task context, **writes the commit message** (this is where the message comes from), commits, pushes the `task/<id>` branch. Shares attribution with the deterministic commit-attribution rule (ADR-0050) — the message generation already exists as `GitService.GenerateCommitMessageAsync` (Haiku); this step is that capability promoted into a configurable pipeline step, not a second commit path.

### 4.2 Integration / Merge agent (LLM step)

Merges `task/<id>` → `integrationBranch` (or opens a PR, §5). On merge conflicts it **resolves them with judgement** — that is the whole reason it is an LLM step and not a bare `git merge`. The mechanical git calls (`worktree add/remove`, `rebase`, FF-`merge`, `push`, PR API) are deterministic tooling in `GitService`; *what* gets committed / how a conflict is resolved is the agent's decision via prompt. Bounded by [ADR-0032](../architecture/decisions/adr-archive.md#adr-0032) (agent classifies/produces a schema-validated result; the rule engine decides halt/reopen/escalate).

## 5. Parallelisability gate (the "smart orchestrator", not "always parallel")

Two cheap stages, both rendered on the timeline (§6):

1. **Preparation step (once per task, stored on the task):** `exclusive?` (too big / cross-cutting ⇒ runs alone — the exception) + `predictedScope` (which paths/areas it will touch). Default is parallelisable; `exclusive` is rare. Reuses the existing prep loop (`OrchestratorPrepRules` / `OrchestratorPrepHostedService`).
2. **Pick-gate (runner, when a slot frees, cheap + fast):** `exclusive` ⇒ run alone; else compare this task's stored `predictedScope` against the scopes of the **currently running** tasks ⇒ `parallel-ok` / `serialize`. This is the gate added at `ProjectRunner.cs:461`: the single `_activeJobId` latch becomes "N slots, admit only if the pick-gate says `parallel-ok`".

## 6. Integration strategies

| Strategy | For whom | Integration agent does |
|---|---|---|
| **`direct-merge`** | Solo / no review gate | rebase `task/<id>` onto `integrationBranch` → FF/merge → delete branch + worktree. A **merge-queue serialises this**: only one branch integrates into `integrationBranch` at a time, even when N tasks run in parallel. |
| **`pull-request`** | Teams / protected branch | push `task/<id>` → open PR against `integrationBranch` (via `gh` / provider API) → the team's review + CI own it. Branch stays until the team merges. |

## 7. Transparency

Every step (pre + core + post) is a first-class timeline entry (ADR-0049 / ASS-560): name, status, duration, model, tokens/cost, artifacts — **plus** the injected prompt contract, the pick decision + rationale, and the git action (branch, commit SHA, merge/PR link). Example pick lines:

- "Slot 2 ← Task B — parallel-ok: disjoint from A (A: `frontend/`, B: `backend/Services/Drift/`)"
- "Task C held SERIAL — conflict with A (`ProjectRunner.cs`)" / "Task D EXCLUSIVE — scope too broad"

## 8. Slicing plan

Dependency-ordered. Each slice is independently shippable/verifiable and names its docking point + the file(s) it touches. Order follows ADR-0052 §10, grounded in the actual code.

> **Slice 0 — crash-safe pickup (PREREQUISITE, already its own card).** Not part of this concept's scope; ADR-0052 gates everything below on it. Do not start §8.2+ in `maxParallelism ≥ 2` mode until `bug-kritisch-crash-safe-pickup…` is closed.

1. **[Done] Config + worktree plumbing in `GitService` (no behaviour change at default).** `maxParallelism` / `integrationBranch` / `integrationStrategy` live in `ProjectSettings` + `ProjectSettingsService` and the runner keeps `maxParallelism = 1` behaviour byte-for-byte. `GitService` now exposes worktree add/remove, rebase, FF-merge, and branch-parameterized `PushShaAsync`; `WorktreeTaskLifecycle` wraps those primitives for `task/<id>` branches. *Acceptance:* settings round-trip and real temp-repo coverage live in `ProjectSettingsServiceTests`, `GitWorktreePrimitivesTests`, and `WorktreeTaskLifecycleTests`.
2. **Worktree + slot model + local lease-per-task.** Generalize the `ProjectRunner.cs:461` `_activeJobId` latch into N slots bounded by `maxParallelism`; one local `PickupLockFile` lease per occupied slot instead of one per project. Pre-step creates `task/<id>` worktree off `integrationBranch`; teardown post-step removes it. The prompt contract (§4) is injected by the pre-step. *Acceptance:* with `maxParallelism = 2`, two disjoint tasks run concurrently each in its own worktree under one backend process; with `= 1`, behaviour is identical to today (`ParallelLanesPickupTests` stays green). This is **not** the multi-machine locking design; do not treat `.pickup-lock.json` as a distributed lease.
3. **[Done] Git steps as runner-owned branch commit + push first.** A worktree run is committed on its `task/<id>` branch by `ProjectRunner.IntegrateWorktreeRunAsync`, then `WorktreeTaskLifecycle.PushTaskBranchWithRetryAsync` pushes that branch to `origin` before local integration. Failed push retries surface as a Warn `task-branch-unpushed` outcome issue on the card and task detail; the run may still integrate locally. *Acceptance:* `WorktreeTaskLifecycleTests.PushTaskBranchWithRetry_PushesTaskBranchToOrigin`, `TaskScannerOutcomeIssueTests.TaskBranchUnpushedMarker_SurfacesWarnOutcome`, and the protocol-pane explanation spec cover the durable branch and visible warning paths.
4. **Parallelisability gate + timeline.** Add the `exclusive?` + `predictedScope` prep step (§5.1) and the pick-gate (§5.2) at the slot admission point. Render the pick decision + rationale as a timeline event (ADR-0049). *Acceptance:* a cross-cutting task is held serial / flagged exclusive with a visible rationale; disjoint tasks admit in parallel.
5. **[Done] Integration step - `direct-merge` + merge-queue.** `ProjectRunner` serializes integration with the per-project merge lock, records the integration pipeline step, and emits `integration-conflict` / `integration-error` outcome issues when the task branch cannot be folded into the work branch. `TeardownIfIntegrated` removes the worktree plus local and remote task branch only after the branch is already merged. *Acceptance:* `WorktreeTaskLifecycleTests` covers direct merge, advanced integration branch rebase, conflict preservation, local cleanup, and remote branch cleanup.
6. **Integration step — `pull-request`.** Push branch + open PR against `integrationBranch` via `gh`/provider API. The only genuinely new external surface. *Acceptance:* a task in `pull-request` mode leaves an open PR and stops; no auto-merge unless D2 says otherwise.

### 8.2C Multi-system follow-up: task leases, shared store, and origin distribution

**Implemented daemon slice (2026-07-10).** The standalone runner now polls an
assignment-aware server claim endpoint and fills bounded host slots (default 2).
The project record owns `executionRunner` and `remoteExecutionEnabled`; the
remote claim path and local in-process pickup read those same fields. Each claim
receives a fenced run lease, moves from `2-ready` to `3-progress`, and runs in a
task-specific linked worktree. This delivers continuous single-server pickup;
the stronger durable shared-store and stale-token-on-every-write requirements
below remain the target for multi-server/high-availability operation.

This is deliberately later than the local worktree/slot slices. Do **not** start a multi-system runner or "agent builder" from this concept without a reviewed design task and close operator supervision. The local slice proves slot admission and worktree isolation inside one backend. Multi-system execution changes the source-of-truth model and must be treated as a separate critical checkpoint.

**Hard prerequisite: one authoritative Task Server.** A local task-folder repository is not a distribution protocol. In multi-system mode, task state, lane transitions, run records, leases, heartbeats, timeline events, and durable log/artifact references live behind a shared Task Store owned by the Task Server. Local folders can exist only as runner caches/projections. There must not be two authoritative writers, and there must not be a "best effort sync" between local file repos. This aligns with the Server/Runner split in [Task Execution & Log Architecture](task-execution-and-log-architecture.md).

**Lease contract.** Lease acquisition is a transactional store operation, not a filesystem lock:

- `AcquireRunLease(taskId, runnerId, requestedTtl)` succeeds only when the task has no unexpired lease and the task is still in an admissible state. It creates a `leaseId`, increments a monotonic `fencingToken`, records `runnerId`, and sets `expiresAt` using server/store time.
- `Heartbeat(leaseId, fencingToken, ttl)` extends the lease only when both the lease id and fencing token still match the current row.
- Every write that can affect task state requires the current `leaseId` and `fencingToken`: log chunk ingestion, timeline append, run completion, lane transition, artifact registration, branch integration, and cleanup decisions.
- Expiry permits a new runner to acquire a new lease with a higher fencing token. The old runner may still be alive, but its later writes are rejected as stale. This is the split-brain guard; TTL without fencing is not sufficient.
- Lease expiry, heartbeat failure, stale-token rejection, and re-acquisition are first-class timeline/runtime events with runner id, lease id, fencing token, and reason.

**Store requirements.** The shared store must provide atomic conditional update or compare-and-swap semantics over task/run lease rows. Use the store/server clock for `expiresAt`; runner-local clocks are not authoritative. If the Task Server itself becomes highly available, it must sit on an externally consistent database or consensus-backed store. Do not implement multi-primary locking in application memory.

**Code distribution.** Task code does not travel through the Task Store. It travels through `origin`:

- The server records `origin`, `integrationBranch`, `taskBranch = task/<id>`, base commit, and current task-branch head.
- A runner starts by fetching from origin, then creating or checking out its local worktree from the recorded refs. Handoff after a crash uses `git fetch origin task/<id>`; the new runner does not depend on the crashed runner's disk.
- The commit/push step publishes `task/<id>` before a task can be considered remotely recoverable. If a runner dies before the branch exists on origin, recovery must either restart from the base commit or escalate; do not pretend local unsynced code can be recovered cross-machine.
- Integration uses a separate per-project integration lease or merge queue, also fenced. Only the current integration holder may mutate the integration branch or delete remote `task/<id>`.

**Risks and review gates.**

| Risk | Failure mode | Required mitigation before implementation |
|---|---|---|
| Split brain | Runner A misses heartbeats, Runner B gets the task, then A wakes up and completes stale work | Fencing token checked on every write and integration action; stale writes are rejected and surfaced |
| Dual source of truth | Local task folders and the Task Server both accept mutations | One shared Task Store is authoritative; local folders are cache/projection only; all mutations go through server APIs |
| Lease TTL tuning | Too short causes false takeover; too long leaves dead tasks stuck | Configurable TTL, heartbeat interval, grace policy, visible lease state, and tests for slow heartbeat/recovery |
| Network partition | Runner can keep editing while disconnected from the server | Runner may continue local process only until it cannot renew; completion/state writes fail without the current lease |
| Origin drift | Remote branch is deleted, force-pushed, or points at an unexpected SHA | Expected-SHA checks, no force push by default, protected branch rules, explicit human escalation on mismatch |
| Integration race | Two runners merge or delete remote branches concurrently | Fenced integration lease/merge queue per project and integration branch |
| Mixed runner versions | Old runner omits fencing fields or does not understand branch handoff | Runner capability registration and minimum-version checks before lease acquisition |
| Credentials spread | Every runner needs git and Task Server access | Per-runner identities, least-privilege git tokens, audit events for lease and branch mutations |
| Large artifacts/logs | Store becomes a blob bucket | Store metadata and pointers; artifact/log payloads use the log/artifact ingestion path from the Server/Runner split |

**Acceptance tests for the multi-system slice.** Before enabling it outside a supervised test setup:

- Two runner processes race the same ready task; only one gets a lease.
- Runner A acquires a lease, loses heartbeat, Runner B acquires a higher fenced lease, and Runner A's stale completion/log/integration writes are rejected.
- Server restart preserves lease rows and requeues only after stored expiry.
- Crash handoff on a pushed `task/<id>` branch succeeds from `origin`; handoff before the first push escalates or restarts from base with an explicit reason.
- Two completed task branches contend for integration; the merge queue serializes them and rejects stale integration tokens.
- Shared-store mode has no direct task-folder state mutation path outside the Task Server API.

## 9. Open decisions — recommended defaults (operator may override)

Resolved here with rationale per the project's "decide-with-defaults, don't loop on questions" practice. These are the discussion surface; none is locked.

1. **D1 — `pull-request` target: always `develop`, or also `main`?** → **Default: target `integrationBranch` (`develop`).** `main` stays released/protected (§3). Allow `main` only if a project sets `integrationBranch = main` explicitly. Rationale: one rule ("PRs target the integration line"), no special-casing.
2. **D2 — may the integration agent auto-merge on green CI, or human-only?** → **Default: human-only for `pull-request`; auto-FF for `direct-merge`.** `pull-request` exists *because* a team wants the review gate — auto-merging would defeat it. `direct-merge` is the solo path and already implies "no review gate", so FF-after-green is consistent. Make auto-merge an explicit future opt-in, not a default.
3. **D3 — default branch per user or per project?** → **Default: per project, overridable per `OwnerClientId`.** Matches the card (§2) and the existing per-project-settings home; a multi-user workspace can still override per user without changing the default.
4. **D4 — conflict resolution: fully automatic, or escalate past some complexity?** → **Default: agent attempts; escalate to human review past a complexity threshold.** Route through ADR-0032 — the agent produces a schema-validated proposal; the rule engine escalates (rather than merges) when confidence is low or the conflict spans > N files/hunks. Never silently force a resolution. Threshold starts conservative and is tuned from timeline data.
5. **D5 — resource/quota cap at high `maxParallelism` (N× CLIs = N× tokens).** → **Default: cap effective parallelism by the existing token/quota guard.** `EnforceQuotaCapsOnActiveJob` (ProjectRunner) already exists per-job; extend it to a per-project budget so a high `maxParallelism` cannot blow the quota — admit fewer slots when the budget is tight. Surface the throttle as a timeline note.

## 10. Sibling cards to reconcile before creating new tasks

The card says **"Kein Duplikat anlegen."** Several existing cards overlap this scope. Before spinning out the §8 slices as tasks, reconcile against these (verify each card's current lane/state first — task state drifts):

- `parallel-pipeline-phases-and-in-task-iteration`
- `parallel-review-preparation-progress-pickup`
- `paralleles-starten-von-tasks`
- `feature-task-as-pipeline-pre-and-post-processing-steps-per-step-model-tokens-parallelism`
- `pickup-loop-progress-first-strict-iteration`
- `project-chat-fix-and-parallel-usage`

Recommendation: fold any that match a §8 slice into that slice rather than creating a parallel card; close or relabel the rest as superseded by ADR-0052 + this concept.
