# Task Integration and the Worktree/Merge Workflow

Status: Current implemented behaviour (verified against code on the `main`/dev checkout). Some aspects are under review; see "Known sharp edges".

This page explains how a finished task's work reaches the integration branch (`develop` by default). It documents what the code does today, not any proposed redesign.

## The one rule for agents

The run agent does NO git. You do not branch, stage, commit, push, or merge. The platform owns all git operations as pipeline steps (ADR-0052). Just edit files in your working directory; the runner captures, commits, and integrates your changes for you. If your run leaves the shared main checkout dirty in an unexpected way during a parallel run, that is reported as a containment violation, not committed.

## Worktree + branch model

- Branch naming: every isolated task runs on `task/<id>` (`WorktreeTaskLifecycle.BranchFor`).
- Whether a `task/<id>` branch and worktree exist at all depends on `MaxParallelism`:
  - `MaxParallelism == 1` (default, sequential): NO worktree, NO `task/<id>` branch. The agent edits the shared main checkout directly, on whatever branch it has checked out.
  - `MaxParallelism >= 2` (parallel): each run gets an isolated git worktree on its own `task/<id>` branch, cut from `IntegrationBranch`. `WorktreeTaskLifecycle.Prepare` / `PrepareOrReuse` create/reuse it.
- File: `backend/Features/Runner/WorktreeTaskLifecycle.cs`.

## The git pipeline steps

Committing, integrating, and merging are catalogue steps, not agent actions. Relevant step ids (`backend/Features/Pipeline/PipelineCatalogue.cs`):

- `post-integrate-merge` ("Integrate merge") - the automatic parallel integration at run end. Not deferred.
- `post-git-commit-attribution` ("Git commit attribution") - pins a task's commit set to its own run windows; runs on the `3-progress -> 4-auto-review` transition.
- `post-merge-into-develop` ("Merge into Develop") - `Deferred = true`. Sits "pending" in the pipeline view until the operator accepts the task.

## Commit and push timing

Driven by `TaskTransitionService.MoveAsync` (`backend/Features/Tasks/TaskTransitionService.cs`), read live from `ProjectSettings` on every transition.

- Auto-commit (sequential path): on `3-progress -> 4-auto-review`, if `AutoCommit == true` and the mode is not read-only, `TryAutoCommitAsync` commits the dirty tree, scoped to this task's run windows, and stamps the SHA on the job folder (lines ~101-137). Read-only modes (planning/research) skip every git side effect.
- Auto-commit (parallel path): the per-transition auto-commit does not apply; instead `ProjectRunner.IntegrateWorktreeRunAsync` commits the agent's edits onto `task/<id>` at run end via `GitService.WorktreeRunCommit` (line ~933).
- Push timing is governed by `AutoPushStrategy`:
  - `never` - commits stay local.
  - `on-completed` - pushed when the task reaches `6-completed` (queued to a background push worker; a periodic backstop covers shutdown drops).
  - `always-immediate` (default) - queued for push as soon as the platform-owned commit exists. Network work stays off the transition/run path.
- Integration-branch commits are also queued for push after merge. A final push failure is recorded as the typed `managed-repo-push-failed` operator-feed event; verified remote status remains ahead until a retry succeeds.
- Workspace artifact commits use the global `WorkspaceArtifacts:AutoPushEnabled` switch (default `true`) and `WorkspaceArtifacts:PushRetrySeconds` retry base (default `30`). Every successful artifact commit queues an immediate `origin/main` push.

## The deferred "Merge into Develop" step (background acceptance path)

The merge into `develop` is operator-triggered, not automatic, in sequential mode.

- Trigger: a task moving `5-human-review -> 6-completed` (operator accepts a done-green task). `TaskTransitionService.MoveAsync` stamps `integrationpending` and writes an immutable request to `AcceptedIntegrationQueue`; the HTTP request does not wait for merge or build. This normal queued state does not emit an accept warning.
- Execution: `AcceptedIntegrationWorker` calls `MergeIntoDevelopRunner.Run` (`backend/Features/Pipeline/MergeIntoDevelopRunner.cs`) after acceptance. The runner selects the accepted delivery from the run contract. A local run uses `task/<id>`. A remote run uses the fenced `ResultRef` and `ResultSha` from `logs/review-subject.json`, fetches that exact `runner/<runner>/<task-key>` ref plus the configured integration branch from origin, verifies that the delivery tip still equals the reviewed SHA, and fast-forwards or creates the local integration branch from its origin ref. For a normal integration target it merges the immutable SHA. For `main` it runs the mandatory full suite on that immutable SHA before a fast-forward. A divergent local integration branch is reported instead of silently falling back to another target. The outcome is recorded into the job's `pipeline-execution.json` (pending -> passed/failed/skipped). Successful integration clears the pending marker and hands the released SHA and recorded target branch to the existing integration push worker.
- It runs AFTER the lane move has already landed and never throws, so it cannot undo or delay the transition.
- Outcomes: `Merged` / `AlreadyMerged` -> Passed; `NoTaskBranch` -> Skipped; `PushedForReview` -> Skipped when the project explicitly uses the pull-request strategy; `Conflict` -> Failed (merge aborted, working tree left clean, conflicted files and the recovery action are listed); error -> Failed.
- Restart recovery: `AcceptedIntegrationBackstopHostedService` scans Completed and already archived local and remote deliveries and consumes the same `TaskIntegrationStatusService` verdict as the board. Exact reviewed `ResultSha` ancestry is primary Git truth; the latest merge step is diagnostic, so a stale Passed step cannot hide a missing delivery and a later out-of-band integration self-heals an older failure. A local delivery is eligible without `review-subject.json`. For remote deliveries the resolver distinguishes a curated manual rebase from the narrower crash window where the exact fenced delivery SHA already landed but the process died before recording the merge and queueing its push; in that window it records `already-merged` and restores the push fact. It does not replay a currently non-integrated conflict, a local `no-branch` no-op, or a pull-request handoff.
- Conflict recovery: a conflict card renders **Rebase & retry** next to its red
  integration badge. The action calls
  `POST /api/tasks/{id}/integration/rebase`, appends a focused steer prompt,
  moves the card to the top of Ready, and lets the assigned remote runner
  resume its existing delivery ref, rebase it onto the current integration
  branch, resolve conflicts, and return a new fenced result for acceptance.
- Push durability: `IntegrationPushQueue` remains in memory to keep network work off the request path. `IntegrationPushBackstopHostedService` re-drives any passed merge with a non-terminal push step after restart, so queue loss cannot leave the integration branch local-only.
- Shutdown drain: once the accepted worker enters merge + build gate + possible rollback, it ignores host cancellation until that consistency boundary reaches a terminal result. `/healthz/drain` returns `gate-busy` during that window. The external stable restart watcher waits up to `ATP_GATE_DRAIN_TIMEOUT_SECONDS` before it invokes the hard update/restart path.

### Incident: 2026-07-24 bulk acceptance

The operator accepted 35 remote-runner cards. The acceptance hook did fire:
each sampled card recorded `post-merge-into-develop` immediately after the lane
move. Every sampled step returned `no-branch`, however, because the merge runner
constructed `task/<slug>` while the remote runner had published the reviewed
delivery as `runner/agent-runner-01/<task-key>`. Since no local merge succeeded,
`IntegrationPushQueue` correctly received no item. Its separate defect was that
an item already enqueued after a successful merge existed only in the process
channel and had no restart backstop. The warning and `integrationpending` tag
made the failure visible, but cleanup compared it with the invalid
pre-normalization spelling `integration:pending`. `SetJobTags` had already
stripped the colon to satisfy the tag-id grammar, so manually integrated cards
continued to display the stale marker. The restart sweeps also originally used
the live-board-only scanner despite claiming archive support, which excluded a
card after it moved to `7-archive`.

A push that reached the worker was not swallowed: its pipeline push step was
recorded as failed and the managed-repository bus failure was emitted. The
silent durability gap was an item lost with the in-memory channel before the
worker could process it.

The correction makes `review-subject.json` the remote acceptance source of
truth, adds archive-inclusive merge and push restart backstops, closes both
restart windows (before the merge and after the local merge but before its
durable record), compares the actual persisted pending tag on both the normal
and recovered success paths, and gives conflicts a typed recovery action.

A read-only `git merge-tree` replay used all eleven reported delivery refs as
incident fixtures. Against the incident head `dfa806689`, all eleven reproduced
conflicts. Against the later `origin/develop` head, AGT-2227, AGT-2234,
AGT-2238, AGT-2240, AGT-2253, AGT-2259, AGT-2261, AGT-2263, AGT-2265, and
AGT-2273 still reproduced conflicts, while AGT-2294 was already an ancestor.
The replay did not mutate `develop`; conflict resolution proceeds through the
steer recovery action above.

The subsequent operator reconciliation landed AGT-2238, AGT-2240, AGT-2253,
AGT-2259, AGT-2261, AGT-2263, AGT-2265, and AGT-2294 in `origin/develop`.
AGT-2227, AGT-2234, and AGT-2273 remain the live pending cases. After this fix
is deployed, the accepted-integration backstop replays their fenced delivery
refs, records the concrete conflict files, and enables the card action for the
focused rebase round. This sequencing is intentional: a pending legacy
`no-branch` record is not rewritten or moved through a task-store filesystem
shortcut.

## The automatic integration at run end (parallel path)

For `MaxParallelism >= 2`, integration happens automatically at run finalization, before the task ever reaches review.

- Entry: `ProjectRunner.IntegrateWorktreeRunAsync` (`backend/Features/Runner/ProjectRunner.cs`, line ~907). Guarded by `if (!run.IsWorktreeRun) return null;` - a no-op for the sequential path.
- Sequence: commit agent edits onto `task/<id>` (`WorktreeRunCommit`) -> push `task/<id>` to origin for portability -> acquire the per-project merge serialization (local `_integrateLock` semaphore + a cross-runner integration lease) -> `WorktreeTaskLifecycle.Integrate`.
- `Integrate` (direct-merge): rebase the worktree onto the `IntegrationBranch` tip, then `git merge --ff-only` into the integration branch checked out in the main checkout. Result history is linear with rewritten SHAs.
- Conflicts: a rebase conflict returns `IntegrationOutcome.Conflict`; the conflicted state can be preserved and escalated to a managed conflict-resolution agent (`CompleteIntegrationAfterResolution`). Unresolved work is left in place.
- `IntegrationStrategy == pull-request`: `Integrate` returns `IntegrationOutcome.PushedForReview` without merging. Operator acceptance also honors this strategy and records the delivery as awaiting a pull request instead of reporting a successful merge.

## Worktree cleanup policy

Teardown is deferred and conditional on the work being merged (`WorktreeTaskLifecycle.TeardownIfIntegrated`, lines ~341-361).

- The runner does NOT tear down per run, so a resume/reissue can reuse the worktree (`PrepareOrReuse`).
- At terminal exit (accept into review / escalate), teardown runs only if `task/<id>` is already an ancestor of `IntegrationBranch`. If it is merged, the worktree and branch (local + `origin/task/<id>`) are removed. If it is NOT merged (e.g. a conflict left for resolution), teardown is skipped and the branch/worktree are preserved.

## Side-by-side: maxParallelism == 1 vs >= 2

| Aspect | `MaxParallelism == 1` (sequential, default) | `MaxParallelism >= 2` (parallel) |
| --- | --- | --- |
| Worktree / branch | None; shared main checkout | Isolated worktree on `task/<id>` |
| Where agent edits land | Current branch of shared checkout (usually `develop`) | `task/<id>` branch in the worktree |
| Commit timing | On `3-progress -> 4-auto-review` (if `AutoCommit`), scoped to run windows | At run end via `WorktreeRunCommit` (whole worktree) |
| Merge timing | Deferred; on operator accept `5-human-review -> 6-completed` | Automatic at run end, before review |
| Merge trigger | Operator acceptance | Run finalization (no human gate) |
| Merge command / history | `git merge --no-ff` (one revertable merge commit, original SHAs) | rebase + `git merge --ff-only` (linear, rewritten SHAs) |
| Serialization | Implicit (single active slot) | `_integrateLock` semaphore + cross-runner integration lease |
| Conflict handling | Abort, record Failed, operator resolves manually | Preserve and escalate to a managed resolver; block teardown if unresolved |
| Push of `task/<id>` to origin | n/a (no task branch) | Pushed at run end for portability |
| Pipeline step | "Merge into Develop" (`post-merge-into-develop`, deferred) | "Integrate merge" (`post-integrate-merge`, automatic) |
| Worktree cleanup | n/a | Deferred; only if branch is an ancestor of `IntegrationBranch` |

## Per-project settings that affect integration

Defined in `backend/Shared/Models/ProjectSettings.cs`. Read live on each transition.

| Setting | Type / default | Effect |
| --- | --- | --- |
| `MaxParallelism` | `int`, `1` | Concurrency slots; clamped to `>= 1`. `1` = sequential (no worktree). `> 1` = worktree-isolated parallel. Today this also selects the entire integration path (see sharp edges). |
| `IntegrationBranch` | `string`, `develop` | Target branch that `task/<id>` branches fork from and merge into. Used by both the parallel run-end integration and the deferred merge. In pure-sequential mode the value is largely unused. |
| `IntegrationStrategy` | `string`, `direct-merge` | `direct-merge` or `pull-request`. Both run-end integration and operator acceptance consult it. Acceptance records `pull-request` deliveries as awaiting review instead of claiming that they merged. |
| `AutoCommit` | `bool`, `true` | When true, auto-commit dirty changes on `3-progress -> 4-auto-review` (sequential). Read-only modes skip it. |
| `AutoPushStrategy` | `string`, `always-immediate` | `never` / `on-completed` / `always-immediate` - when committed work is queued for push to origin. |

## Known sharp edges (under review)

These behaviours are real today and being reviewed. See the configuration analysis: [./task-integration-merge-config-analysis.html](./task-integration-merge-config-analysis.html).

- Parallelism coupling: `MaxParallelism` is perceived as a throughput knob, but flipping it `1 <-> >=2` also silently changes the commit target, merge timing/trigger, merge command and history shape, conflict handling, and what "Accept" means. `IntegrationBranch` and `IntegrationStrategy` are not exposed in the frontend.
- Auto-commit on transition: in sequential mode the auto-commit lands directly on the shared checkout's current branch with no `task/<id>` branch, so the deferred "Merge into Develop" step often records `NoTaskBranch -> Skipped` even though the work already landed. The completed-push target can also diverge from the merge target. The exact on-transition git state is under review.

## Branch cleanup (Project Hub Git-Management, AGT-2009)

Over time a project repository accumulates dead refs: merged `task/*` branches
(local and `origin/*`), operational `refs/backups/*` snapshots, and stale
worktree registrations whose folders were removed out-of-band. The Project Hub
Git View exposes an operator-driven cleanup that prunes only what has already
landed.

- Analysis is a read-only **dry-run plan** (`GitCleanupService.BuildPlan`, endpoint
  `GET /api/git/cleanup/plan?project=<name>`). It classifies every `task/*` branch
  (local + remote), every `refs/backups/*` ref and every stale worktree against the
  project's `IntegrationBranch`: `merged` / `unmerged` / `stale`. Each row carries
  its merge evidence and a keep/delete reason.
- Execution (`GitCleanupService.Execute`, endpoint `POST /api/git/cleanup/execute`)
  acts only on an operator-confirmed subset. It re-derives eligibility from a fresh
  plan and re-checks `merge-base --is-ancestor` immediately before each branch/ref
  delete, then dispatches the existing GitService primitives (`DeleteBranch`,
  `DeleteRemoteBranch`, `DeleteRef`, `WorktreePrune`). The result reports
  `n deleted / m kept` with per-item reasons.
- **AGT-1945 invariant**: only GEMERGTES is ever deleted. Unmerged branches and
  backup refs whose commit is not yet contained in the integration branch are never
  touched, and a branch checked out in a live worktree is kept (remove the worktree
  first). The guard is server-side, so a stale or crafted request cannot drop
  unmerged work. `refs/backups/*` is dropped only when its commit is an ancestor of
  the integration branch.
- UI: `app-project-git-cleanup` (a collapsible section inside `project-git-panel`)
  renders the plan with per-row checkboxes (eligible pre-selected, ineligible
  disabled with the reason), a two-step confirm before deletion, and the result
  report. Coverage: `GitCleanupServiceTests` (backend, temp repo) and
  `project-git-cleanup.component.spec.ts` (frontend).
- **Automation status**: cleanup is operator-triggered only. The optional
  "auto-cleanup after a successful merge step" hook (the counterpart to the
  push-on-merge from AGT-1999) is intentionally **not** wired yet - there is no
  automatic ref deletion anywhere in the pipeline. When it is added it should reuse
  `GitCleanupService.BuildPlan`/`Execute` unchanged (so the AGT-1945 guard keeps
  holding) and gate on the same per-project setting as push-on-merge; until then,
  removing merged refs is always an explicit operator action.

## See also

- `docs/concepts/parallel-task-execution.md` - parallel execution model, integration strategies, merge-queue.
- `docs/concepts/release-semantics.md` - the decided integration and release model (supersedes the retired `git-branching-integration-zielbild.md` draft). The target three-tier branching model (`task/<id>` -> `develop-local` -> `develop`) described in that draft was not carried forward; the `develop-local` tier remains a target, not yet implemented.
- ADR-0052 in `docs/system/architecture/decisions/adr-archive.md` - the parallel-execution decision and the "run agent does no git" contract.
