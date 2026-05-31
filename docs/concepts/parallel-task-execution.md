# Concept — Parallel Task Execution: worktrees, branch model (`develop`), git as pre/post pipeline steps, merge/PR

**Status.** Concept (sharpened design + slicing plan). This is the "full design" home that [ADR-0052](../architecture-decisions.md#adr-0052---intra-project-parallelism-is-now-an-opt-in-orchestrator-gated-capability-2026-05-31) points to. It is a design deliverable, not an implementation. Implementation is sliced in §8 and is gated on the crash-safe-pickup task.

**Source card.** `konzept-parallele-task-verarbeitung---worktrees-branch-modell-develop-git-als-prepost-agent-steps-mergepr` (ASS-589). Discussion with the operator on 2026-05-31.

**Date.** 2026-05-31.

---

## 0. What is already decided / already done (premise correction)

The source card frames the intra-project-parallelism non-goal as a *hard product boundary that this concept must reverse*, citing `IntakeRunner.cs:243` and `OrchestratorPrepRules.cs:107` as the enforcing guards. **That reversal already landed.** Recording it here so no slice re-does it:

- **The policy is already reversed.** [ADR-0052](../architecture-decisions.md#adr-0052---intra-project-parallelism-is-now-an-opt-in-orchestrator-gated-capability-2026-05-31) (Accepted, 2026-05-31) supersedes [ADR-0001](../architecture-decisions.md#adr-0001---sequential-per-project-parallel-across-projects-2026-04-15)'s "no intra-project parallelism, no worktrees, no branch-per-task". Bounded intra-project parallelism is now an opt-in, orchestrator-gated capability.
- **The intake/prep guards are already gone.** `IntakeRunner.CheckBlocked` now `return null;` (the "worktree"/"parallel coding"/"branch-per-task" phrase blockers were removed) and `OrchestratorPrepRules.HasOutOfScopeToken` now `return false;`. The cited line numbers no longer point at a guard — they point at the *removed-guard* comments referencing ADR-0052. **No slice should "remove the guard"; it is done.**
- **`maxParallelism` is referenced but not implemented.** It exists today only in comments/ADR text. It is not a field on `ProjectSettings`, not read by the runner, and the runner still enforces one active job per project (see §1).

So this concept is *purely additive* on an accepted decision. The remaining work is mechanism, not policy.

## 1. The foundation this docks onto (do NOT duplicate)

The product already has most of the scaffolding. Each item below is a docking point, with the exact code/ADR it lives in.

| Capability | Where it lives | How this concept uses it |
|---|---|---|
| **Pre/core/post pipeline** (first-class steps, per-job `pipeline-execution.json`) | [ADR-0045](../architecture-decisions.md#adr-0045---task-processing-as-a-first-class-pipeline-of-pre--core--and-post-steps-2026-05-29); `backend/Services/Pipeline/PipelineCatalogue.cs` (`StepKind` = Module/Core/Aspect/Orchestrator/Tool) | Git steps are new pipeline steps, not new infrastructure. Worktree-create is a **pre** step; commit/integration/cleanup are **post** steps. |
| **Per-project pipeline config** (reorder, enable, per-step model/mode) | ADR-0051 (proposed) + card `prepost-processing-steps-project-level-config…`; `ProjectSettings.PipelineSteps`, `ProjectSettingsService.SetPipelineStep`, `PipelineStepConfigResolver` | Home of the step definitions + the new `integrationStrategy` field. The git steps appear here as configurable steps. |
| **Deterministic commit attribution** (commit-to-task binding, `scripted` post-step) | [ADR-0050](../architecture-decisions.md#adr-0050---commit-attribution-regel-deterministic-commit-to-task-binding-2026-05-29); card `feature-post-step-git-commit-attribution-deterministic-commit-to-task-binding` | The Commit+Push agent step (§4) is the LLM-message sibling of this deterministic binding. They share attribution; do not fork it. |
| **Unified timeline ledger** (`timeline.jsonl`, `*_step_started/finished` kinds) | [ADR-0049](../architecture-decisions.md#adr-0049---self-contained-tasks-and-a-unified-timelinejsonl-ledger-2026-05-29) (ASS-560); card `architecture-self-contained-tasks--unified-timeline-view…` | Every git step + the pick decision render here (§6). No new event store. |
| **Completion loop** (retry-until-done, budgeted) | ASS-566; card `epic-orchestrator-completion-loop…` | An `orchestratorReaction: review` integration step that returns `reopen` ticks the loop budget. The loop *wraps* the pipeline (ADR-0051 §7). |
| **Git plumbing** | `backend/Services/GitService.cs` | Has `status`/`diff`/`Commit` (`add -A` + `commit -F -`)/`AutoCommitAsync`/`GenerateCommitMessageAsync` (Haiku) /`PushShaAsync`/commit-attribution helpers. **Missing:** `worktree add/remove`, `rebase`, branch FF-merge, branch-parameterized push (today `PushShaAsync` hardcodes `…:refs/heads/main`), PR creation. |
| **Per-project serialization gate** | `ProjectRunner.cs:461` — `if (_processing || _activeJobId != null) return;` (comment at 472–474: "one coding CLI per project at a time") | This single `_activeJobId` latch is the thing `maxParallelism` generalizes into N slots. |
| **Pickup lease primitive** | `PickupLockFile` / `PickupLockOwner` (disk-backed `.pickup-lock.json`) | Becomes one lease per running task/slot instead of one per project. |
| **Crash-safe pickup** (PREREQUISITE) | card `bug-kritisch-crash-safe-pickup---eine-task-darf-niemals-den-backend-host-killen` | ADR-0052 gates this concept on it: N concurrent runs widen the crash surface. Must land first. |

## 2. Configuration (project level, `project-settings.json`)

New `ProjectSettings` fields (alongside `OrchestratorModel`, `AutoCommit`, `PipelineSteps`, …), all with a serial-preserving default:

| Field | Type | Default | Meaning |
|---|---|---|---|
| `maxParallelism` | int (1–N) | **1** | Concurrent worker slots for this project. `1` = today's serial behaviour, byte-for-byte. |
| `integrationBranch` | string | **`develop`** | Branch tasks branch from and integrate into. `main` stays released/protected. Per-user override keyed on `OwnerClientId` (see open decision D3). |
| `integrationStrategy` | enum | `direct-merge` | `direct-merge` \| `pull-request` (§5). |

"Slot / worker / lane" is a **runner** concept. Git only knows repo / branch / commit / worktree. The runner owns N slots; each occupied slot maps to one worktree + one `task/<id>` branch.

## 3. Branch model + isolation

- **Integration line = `integrationBranch`** (default `develop`). `main` = released.
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

Merges `task/<id>` → `integrationBranch` (or opens a PR, §5). On merge conflicts it **resolves them with judgement** — that is the whole reason it is an LLM step and not a bare `git merge`. The mechanical git calls (`worktree add/remove`, `rebase`, FF-`merge`, `push`, PR API) are deterministic tooling in `GitService`; *what* gets committed / how a conflict is resolved is the agent's decision via prompt. Bounded by [ADR-0032](../architecture-decisions.md#adr-0032) (agent classifies/produces a schema-validated result; the rule engine decides halt/reopen/escalate).

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

1. **Config + worktree plumbing in `GitService` (no behaviour change at default).** Add `maxParallelism` / `integrationBranch` / `integrationStrategy` to `ProjectSettings` + `ProjectSettingsService` (mirror the existing `SetOrchestratorModel` shape) + the settings endpoint/UI. Add `GitService` primitives: `WorktreeAdd(branch, fromRef)`, `WorktreeRemove(path)`, `RebaseOnto`, FF-`Merge`, and **branch-parameterize `PushShaAsync`** (today it hardcodes `refs/heads/main`). Pure capability; `maxParallelism = 1` keeps current behaviour byte-for-byte. *Acceptance:* settings round-trip; new `GitService` methods unit-tested against a temp repo (extend the `WorktreeIsolationTests` harness — note that file currently tests the *shared-tree hygiene* gate, not real worktrees, so this adds the first real-worktree coverage).
2. **Worktree + slot model + lease-per-task.** Generalize the `ProjectRunner.cs:461` `_activeJobId` latch into N slots bounded by `maxParallelism`; one `PickupLockFile` lease per occupied slot instead of per project. Pre-step creates `task/<id>` worktree off `integrationBranch`; teardown post-step removes it. The prompt contract (§4) is injected by the pre-step. *Acceptance:* with `maxParallelism = 2`, two disjoint tasks run concurrently each in its own worktree; with `= 1`, behaviour is identical to today (`ParallelLanesPickupTests` stays green).
3. **Git steps as LLM agents — Commit+Push first.** Promote `GenerateCommitMessageAsync` + commit + push into a configurable Commit+Push **post-step** that operates on the worktree's `task/<id>` branch. Docks onto ADR-0050 / `feature-post-step-git-commit-attribution…` (shared attribution, one commit path). *Acceptance:* a finished run produces a committed+pushed `task/<id>` branch with an LLM-authored message; attribution matches the deterministic rule.
4. **Parallelisability gate + timeline.** Add the `exclusive?` + `predictedScope` prep step (§5.1) and the pick-gate (§5.2) at the slot admission point. Render the pick decision + rationale as a timeline event (ADR-0049). *Acceptance:* a cross-cutting task is held serial / flagged exclusive with a visible rationale; disjoint tasks admit in parallel.
5. **Integration step — `direct-merge` + merge-queue.** Integration agent (§4.2): rebase→FF→teardown, serialised by a per-project merge-queue. Conflict resolution bounded by ADR-0032; `review` reopen ticks the ASS-566 loop budget. *Acceptance:* N parallel branches integrate into `develop` one at a time, conflicts resolved or escalated with a timeline trail.
6. **Integration step — `pull-request`.** Push branch + open PR against `integrationBranch` via `gh`/provider API. The only genuinely new external surface. *Acceptance:* a task in `pull-request` mode leaves an open PR and stops; no auto-merge unless D2 says otherwise.

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
