# Task Integration and the Worktree/Merge Workflow

Status: Current implemented behaviour (verified against code on the `main`/dev checkout). Some aspects are under review; see "Known sharp edges".

This page explains how a finished task's work reaches the effective integration branch. A project setting selects an existing local or remote branch; otherwise repository truth from `origin/HEAD` selects the branch. It documents what the code does today, not any proposed redesign.

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

- Trigger: accepting a done-green task first reads the Git-derived card integration status. An already-integrated card moves directly to Completed without a fetch, merge runner, or build gate; the deferred merge step records `Skipped`, never `Passed`, because no integration command ran. Other cards start a transaction inside `5-human-review`: `TaskTransitionService.MoveAsync` resolves the target through `GitService.ResolveIntegrationBranch`, sets phase `integrating`, stamps the internal `integrationpending` recovery marker, writes `integration_started` to `timeline.jsonl`, and refreshes `origin/<integration-branch>`. The resolver keeps an existing configured local or remote branch, but replaces a missing legacy `develop` candidate with the repository default from `origin/HEAD`. The post-fetch status check still detects an out-of-band remote integration without enqueueing the merge or build gate. Otherwise the request enqueues `AcceptedIntegrationQueue`; it does not wait for merge or build, and the card does not enter Completed while integration is pending.
- Delivery ref: `DeliveryRefResolver` reads card truth in this order: immutable result ref from `review-subject.json`, an attributed `commits[].branch`, `runner/<executor>/<task-key>` from the fenced review subject, then the legacy `task/<slug>` fallback. The merge path does not reconstruct a remote delivery from the folder slug. Remote refs are fetched and fenced to the reviewed result SHA before merge.
- Execution: `AcceptedIntegrationWorker` calls `MergeIntoDevelopRunner.Run` (`backend/Features/Pipeline/MergeIntoDevelopRunner.cs`). Immediately before any merge or release gate, the runner fetches the configured integration branch and fast-forwards a stale local branch. It leaves a local-ahead branch intact, reports a divergent branch without overwriting either tip, and records the outcome in `pipeline-execution.json`.
- Commit: only `Merged`, `AlreadyMerged`, or a pre-existing Git-derived `integrated` status completes the acceptance transaction. The worker clears phase and pending marker, writes `integration_succeeded`, and moves the card to `6-completed`. `NoTaskBranch`, `Conflict`, `GateFailed`, and `Error` clear the phase, retain the pending recovery marker, write `integration_failed`, and leave the card in Human Review. If a legacy accept path already placed the card in Completed, both the worker and restart backstop move it back to Human Review. The red badge includes the concrete outcome, such as `Failed: NoTaskBranch`, and the application-owned `## Acceptance integration` section in `status.md` preserves the outcome, target lane, integration branch, reason, and timestamp. Immediate integration failures use the consistent `IntegrationFailed` transition outcome, which the single-card API maps to HTTP `409`; batch results use `integration-failed`. A configured pull-request handoff also remains in Human Review until target-branch membership is real.
- Explicit completion without integration: operator move requests may set `operatorOverride: true` only with target `6-completed`. The flag is never inferred or defaulted. It bypasses integration for that move and records `operator-override` in pipeline history plus `OperatorOverride` and the supplied reason in `status.md`. Cards that explicitly expect no branch are exempt without an override: report-only or concept modes, epics, `taskType=concept|decision` records, and cards with `noBranchExpected: true`.
- Restart recovery: `AcceptedIntegrationBackstopHostedService` resumes cards in `5-human-review` with phase `integrating` from their phase, marker, and pipeline facts. It consumes the same `TaskIntegrationStatusService` recovery decision as the board status projection, so a stale Passed step cannot overrule missing target-branch membership. It moves cards to Completed only after successful integration. Legacy Completed recovery remains supported and decided failures return it to ordinary Human Review instead of replaying in a loop. Archived cards remain inventory-visible without being silently rewritten.
- Read model and historical inventory: `TaskIntegrationStatusService` recomputes `integration.status` from the attributed `commits[]` membership in the configured target branch and projects `integration.deliveryRef` through the same `DeliveryRefResolver` used by acceptance. Remote `runner/<host>/<KEY>` refs and evidenced local `task/<slug>` refs therefore use one card field; `no-branch` is valid only when neither a delivery ref nor an attributed commit exists. A terminal failed attempt sets `integration.failureOutcome` so the chip names the worker result. The service uses a target-HEAD-fingerprinted ancestor set, accepts valid abbreviated SHAs, and invalidates immediately when the target HEAD moves. Lane, provenance merge records, pipeline success, and curated merge subjects cannot force `integrated`. An out-of-band merge therefore self-heals the card on the next read. Card integration badges, delivery-ref wording, the develop segment, Git-state wording, and acceptance wording consume this computed field; `integrationpending` is not rendered as a second status chip. At startup, `AcceptedIntegrationInventorySweep` emits one console row for each integration-required Completed or archived card whose integration attempt is absent, `Error`, or `NoTaskBranch`; the sweep is read-only.
- Merge-queue terminal history: the Project Hub keeps historical archive outcomes visible without counting them as actionable conflicts. An archived `conflict-skipped` record with a pre-authority review subject that has no `RunAttemptId` is `legacy-unverifiable`; another archived conflict is `superseded` as an in-place merge subject and must be recovered through a new card if its behavior is still required. Both states retain the original integration outcome in their reason. The queue counters are filters for all outcomes, while `conflict` is reserved for work that can still be resolved on its current card.
- Conflict recovery: a conflict card renders **Rebase & retry** next to its red
  integration badge. The action calls
  `POST /api/tasks/{id}/integration/rebase`, appends a focused steer prompt,
  moves the card to the top of Ready, and lets the assigned remote runner
  resume its existing delivery ref, rebase it onto the current integration
  branch, resolve conflicts, and return a new fenced result for acceptance.
- Push durability: `IntegrationPushQueue` remains in memory to keep network work off the request path. `IntegrationPushBackstopHostedService` re-drives any passed merge with a non-terminal push step after restart, so queue loss cannot leave the integration branch local-only.
- Shutdown drain: once the accepted worker enters merge + build gate + possible rollback, it ignores host cancellation until that consistency boundary reaches a terminal result. `/healthz/drain` returns `gate-busy` during that window. The external stable restart watcher waits up to `ATP_GATE_DRAIN_TIMEOUT_SECONDS` before it invokes the hard update/restart path.

### Incidents: 2026-07-24 and 2026-07-28 bulk acceptance

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

The first correction made `review-subject.json` available as a remote acceptance
source and added restart backstops. It did not close the contract: later delivery
refs could live under immutable result refs, accepting still moved the card to
Completed before checking the outcome, and the displayed status could remember a
merge attempt instead of observing the target branch.

The 29 July correction resolves the delivery ref from card truth, makes acceptance
transactional in Human Review, and computes every accepted-card integration status
from target-branch commit membership. This closes the `NoTaskBranch` series from
the 28 July 23:09 acceptance wave and lets out-of-band salvage merges repair the
display without fabricating a new merge attempt.

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
| Merge timing | Deferred; inside the transactional Human Review accept | Automatic at run end, before review |
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
| `IntegrationBranch` | `string`; effective default `origin/HEAD` | Target branch that `task/<id>` branches fork from and merge into. An existing configured local or remote branch wins. If the stored/default candidate does not exist, the resolver uses the repository default branch, so a main-only project never attempts to fetch `develop`. |
| `IntegrationStrategy` | `string`, `direct-merge` | `direct-merge` or `pull-request`. Both run-end integration and operator acceptance consult it. Acceptance records `pull-request` deliveries as awaiting review instead of claiming that they merged. |
| `AutoCommit` | `bool`, `true` | When true, auto-commit dirty changes on `3-progress -> 4-auto-review` (sequential). Read-only modes skip it. |
| `AutoPushStrategy` | `string`, `always-immediate` | `never` / `on-completed` / `always-immediate` - when committed work is queued for push to origin. |

## Known sharp edges (under review)

These behaviours are real today and being reviewed. See the configuration analysis: [./task-integration-merge-config-analysis.html](./task-integration-merge-config-analysis.html).

- Parallelism coupling: `MaxParallelism` is perceived as a throughput knob, but flipping it `1 <-> >=2` also silently changes the commit target, merge timing/trigger, merge command and history shape, conflict handling, and what "Accept" means. `IntegrationBranch` and `IntegrationStrategy` are not exposed in the frontend.
- Auto-commit on transition: in sequential mode the auto-commit can land directly on the configured target branch with no `task/<id>` branch. The computed membership check recognizes this as already integrated and completes acceptance without running a merge. The completed-push target can still diverge from the integration target; that configuration detail remains under review.

## Branch cleanup (Project Hub Git-Management, AGT-2009)

Over time a project repository accumulates dead refs: merged `task/*` branches
(local and `origin/*`), operational `refs/backups/*` snapshots, and stale
worktree registrations whose folders were removed out-of-band. Cleanup is an
operator-driven maintenance action that prunes only what has already landed.
It is separate from the Project Hub Git tree, which is strictly read-only.

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
- The existing `app-project-git-cleanup` component can render the plan with
  per-row checkboxes, a two-step confirmation, and a result report, but it is
  not mounted in the Project Hub Git tree. The tree exposes no mutation action.
  Coverage: `GitCleanupServiceTests` (backend, temp repo) and
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
