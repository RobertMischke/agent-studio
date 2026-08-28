---
id: lineage-blocked-integration-push
title: "Integration push blocked on main lineage starves origin/develop, causing infinite reclaim loops"
status: fixed
first-seen: 2026-08-27T00:00:00Z
last-seen: 2026-08-28T09:00:00Z
severity: blocker
category: runner
tags: [git, integration, lineage-blocked, origin-develop, accept-loop, reclaim, backstop]
affects:
  - backend/Features/Pipeline/MergeIntoDevelopRunner.cs
  - backend/Features/Pipeline/AcceptedIntegrationFailurePolicy.cs
  - backend/Features/Tasks/TaskIntegrationStatusService.cs
  - backend/Shared/Models/TaskProvenance.cs
related-tasks: [AGT-2688]
related-adrs: []
---

# lineage-blocked-integration-push

**What.** Overnight 2026-08-27/28: 705 claims, 0 acceptances. The stable backend
log carried 570+ `Auto-push skipped for {JobId} at {Sha} (completed): lineage-blocked`
warnings. Deliveries merged into the backend's local `develop` (the
`post-merge-into-develop` pipeline step recorded `Passed`) but never reached
`origin/develop`. Acceptance resolves the integration branch against
`origin/develop`, so every one of those cards read as integration-`pending`
forever; the accept loop could never close them, and the runner kept
reclaiming and redelivering already-done work, burning the Claude window on a
board that looked merely stalled.

**Why.** `MergeIntoDevelopRunner.PushIntegrationBranchAsync` (the deferred,
off-the-request-path publisher that `IntegrationPushWorker` /
`IntegrationPushBackstopHostedService` drive) ran the
`ImmediateIntegrationLineagePolicy.Decide` lineage guard — "is local `main`
still an ancestor of local `develop` right now" — **before** attempting to
push `develop` at all. That guard exists to protect the `main` leg only: it is
supposed to answer "may `main` fast-forward to this `develop` SHA", never "may
`develop` itself reach `origin`". Because the two questions were folded into
one early `return`, any local `main`/`develop` divergence (a later card's
delivery, a build-gate rollback that rewound `develop`, a manual branch move -
several ordinary and *expected* operational events, not necessarily a second
writer) aborted the push before `git push origin develop` was ever invoked.
`origin/develop` sat stale indefinitely: not because the push failed, but
because it was never attempted.

A second, independent effect made the failure invisible instead of merely
silent: the early `return` skipped `RecordPushStep` entirely, so the
`post-merge-into-develop-push` pipeline step kept whatever status it already
had (usually never-recorded) rather than a terminal `Failed`. Downstream,
`TaskIntegrationStatusService.ReadIntegrationFailure` only ever inspected the
**merge** step (`post-merge-into-develop`, which had legitimately `Passed`),
never the **push** step. A blocked push therefore had no typed failure
anywhere in the chain and fell through to the generic
`IntegrationStatuses.Pending` reading - indistinguishable from an ordinary
in-flight push a few seconds old. Nothing in the system was lying; every
layer was reporting an honest snapshot of its own narrow view, and the
composition of those honest snapshots was a false "still working on it" that
never resolved.

A separate, legacy per-commit push path
(`TaskTransitionService.PushJobCommitsAsync` -> `TryPushCommitAsync`, fired on
every `6-completed` transition when `AutoPushStrategy` is
`AlwaysImmediate`, the default) pushes the raw completed-job SHA straight to
`main` with no branch argument. In a dual-line (`develop` + `main`) project
this is essentially always rejected by
`GitService.ValidateDirectMainAdvance`/`ImmediateIntegrationLineagePolicy.DecideDirectMainAdvance`
(by design - see `docs/operations/git/commit-push-doctrine.md` item 8: raw
task/delivery SHAs must go through the develop-then-main path, not a direct
`main` push) and logs the exact quoted line, `Auto-push skipped for {JobId} at
{Sha} (completed): lineage-blocked`. This path is a secondary, largely benign
noise source - it targets `main`, not `develop`, and its failure does not
touch `origin/develop` or block acceptance - but it is a redundant second
writer racing the same "who publishes to `main`" question the develop-then-main
policy already owns, and it is most of the 570+ warning volume. See
[measures.md](./measures.md) for the recommended (not yet applied)
operational follow-up.

**Workaround.** None available at the git level: an operator had to notice
the stalled board, diff local `develop` against `origin/develop` by hand, and
force a push once main/develop reconverged. `TaskIntegrationRecoveryEndpoints`
only offers rebase recovery for merge conflicts, not push recovery, so there
was no in-product lever for this failure shape.

**Long-term.** Fixed under AGT-2688:
1. `PushIntegrationBranchAsync` now pushes `develop` **unconditionally** before
   ever evaluating the main-lineage guard. The guard only decides whether the
   trailing `main` push proceeds; a blocked `main` leg can no longer prevent
   `develop` from reaching `origin`.
2. When the guard *does* block `main`, the blocked result is recorded through
   the normal `RecordPushStep` path (previously skipped), so the push step
   reaches an honest terminal `Failed` state instead of staying unresolved.
3. A genuinely diverged `develop` push (a real non-fast-forward against
   `origin`, not a `main`-only lineage question) now gets its own distinct
   `push-blocked` verdict instead of being folded into the generic
   `environmental` bucket used for ordinary transient network failures.
4. Both terminal push failures classify through a new
   `AcceptedIntegrationFailureCodes.IntegrationPushBlocked` code.
   `TaskIntegrationStatusService.ReadIntegrationFailure` now also reads the
   push step (previously merge-step-only), so a merged-but-unpublished card
   surfaces as `IntegrationStatuses.ConflictSkipped` with that code -
   distinct from plain `pending` - the moment the push step is recorded.
5. `ResolveAcceptedIntegrationRecovery` treats that exact shape (merge
   `Passed`, push blocked) as `Ignore`: the merge already succeeded, so a
   backstop sweep must not replay it. Retrying the push - and only the push -
   remains `IntegrationPushBackstopHostedService`'s job, on its normal
   15-minute cadence.
6. Backlog reconciliation is automatic, not a one-off script: every prior
   delivery whose push step was never recorded (point 2, pre-fix) reads as
   non-terminal to `IntegrationPushBackstopHostedService.RunOnce`, which
   already re-drives `PushIntegrationBranchAsync` for exactly that set on its
   next tick. Once this fix ships, the existing backstop retries the whole
   affected backlog for free within one interval and republishes `develop`
   unconditionally this time.

See [protocol.md](./protocol.md) for the exact call chain and verification,
[measures.md](./measures.md) for the fix and its test evidence, and
[occurrences.md](./occurrences.md) for the incident log.
