---
id: lineage-blocked-push-noise-and-silent-unpushed-integration
title: "570+ 'Auto-push skipped ... lineage-blocked' warnings and cards that read integrated without ever reaching origin"
status: fixed
first-seen: 2026-08-27T00:00:00Z
last-seen: 2026-08-28T09:00:00Z
severity: blocker
category: state-machine
tags: [git, integration, develop, main, lineage-guard, auto-push, honest-state, backstop]
affects: [backend/Features/Tasks/TaskTransitionService.cs, backend/Features/Tasks/TaskIntegrationStatusService.cs, backend/Features/Pipeline/AcceptedIntegrationFailurePolicy.cs, backend/Features/Git/GitService.cs]
related-tasks: [AGT-2688, AGT-2683]
related-adrs: []
---

# lineage-blocked-push-noise-and-silent-unpushed-integration

**What.** Overnight 2026-08-27/28 the stable backend log carried 570+
`Auto-push skipped for {JobId} at {Sha} (completed): lineage-blocked` warnings and the
operator dossier read this as "0 acceptances, integration stalled" (source dossier:
AGT-W39, `docs/operations/live-improvement-log/index.html`, 2026-08-28 morning entry).
The initial hypothesis was that `integrate-on-delivery` merges each delivery into the
LOCAL `develop` but the push of `develop` itself to `origin` is skipped as
`lineage-blocked`. Re-diagnosis against the actual code found two distinct, real bugs
instead - neither is the hypothesis as stated, but together they produce exactly the
observed symptom.

**Why - bug 1 (the literal log line).** `lineage-blocked` as a `GitPushResult.Status`
string is produced in exactly two places, and both are about advancing `main`, never
`develop`:
`GitService.ValidateDirectMainAdvance` (`DecideDirectMainAdvance`) and
`MergeIntoDevelopRunner.PushIntegrationBranchAsync`'s release-branch guard. The log line
format `"Auto-push skipped for {JobId} at {Sha} ({Reason}): {Status} {Error}"` with
`reason="completed"` uniquely identifies the caller: `TaskTransitionService.TryPushCommitAsync`,
reached from `PushCompletedJobCommitsAsync` (`AutoPushStrategy != never`, fired on every
move to `6-completed` and again by `CompletedPushBackstopHostedService`'s 15-minute
sweep). That call always pushes to a **hardcoded `targetBranch: "main"`**, regardless of
the project's configured integration branch. In a project with both `develop` and `main`
(the dual-line / develop-then-main topology; per `docs/operations/develop-main-promotion.md`
promotion to `main` is a separate operator/cron train, not a per-card push), a raw
completed-job commit is never the exact published `develop` tip once it has been folded
by a `--no-ff` merge - so `DecideDirectMainAdvance` blocks it, correctly, **every single
time**, for **every single completed card**, on both the initial push and every 15-minute
backstop sweep. The guard is correct (per AGT-2688's own framing: "the guard is correct
to refuse a non-fast-forward; the bug is the divergence, not the guard" - here there is no
real divergence at all, just a doomed, redundant attempt). The actual `develop` push
(`MergeIntoDevelopRunner` → `IntegrationPushQueue` → `IntegrationPushWorker`) already
owns publishing this work through the correct, configured branch; this second push
mechanism duplicates it, always fails, and burns a `git fetch` + guard round-trip and a
Warning-level log line for zero possible benefit. This is the actual, mechanical source
of "570+ lineage-blocked warnings" - it does not gate acceptance (the move to
`6-completed` already succeeded before this push is even attempted) and is not itself a
stall.

**Why - bug 2 (the actual stall risk, found while verifying bug 1 could not explain "0
acceptances" on its own).** `TaskIntegrationStatusService.ComputeRepoIntegration` computes
its ancestor set from **both** the local integration branch and `origin/<branch>` combined
(`[integrationBranch, "origin/" + integrationBranch]`), so a commit merged only into LOCAL
`develop` - with its deferred push to origin still queued, retrying, or genuinely blocked -
already reads as fully `integrated` on the board and to every downstream consumer that
reads this status, with no distinction from a commit that has actually reached origin.
`AcceptedIntegrationBackstopHostedService.ResolveAcceptedIntegrationRecovery` short-circuits
to `Finalize` (a no-op once the card is already `6-completed`) whenever
`status.Status == Integrated`, so a card whose push to origin never lands is *silently
finalized on its very next sweep* - the backstop stops retrying the missing push, and
nothing else in the system was ever watching origin specifically. This is the real
mechanism that would starve the ~7-card backlog described in AGT-2688 and, at larger
delivery volume, could plausibly read externally as "acceptances aren't landing" even
while the internal board shows cards as `Completed` / `integrated`.

**Fix (AGT-2688).**
1. `TaskTransitionService.TryPushCommitAsync` now skips the direct-to-`main` push
   entirely when the project has a `develop` line (`DirectMainPushIsRedundant`, mirroring
   `MergeIntoDevelopRunner.HasDevelopLine`'s own check), logging a calm Info line instead
   of an alarming Warning. The develop-then-main integration path already owns publishing
   the work; the guard blocking it was correct, the redundant attempt was the bug.
2. `TaskIntegrationStatusService` now also computes an origin-only ancestor set
   (`GitService.HasOriginRemote` + an `origin/<branch>`-only `TryGetAncestorShaSet`). A
   commit that is a local ancestor but NOT an origin ancestor, with a recorded, terminal
   `MergeIntoDevelopPushStepId` failure, now classifies as `conflict-skipped` with the new
   `AcceptedIntegrationFailureCodes.PushBlocked` (`integration-push-blocked`) code instead
   of silently reading as `integrated`. A push that simply has not run yet (no recorded
   step) still reads `integrated`, honestly, and is not alarmed prematurely.
3. That reclassification alone fixes `ResolveAcceptedIntegrationRecovery`: it no longer
   matches the `Finalize` shortcut, falls through to `Retry`, and the existing 15-minute
   `AcceptedIntegrationBackstopHostedService` sweep keeps re-running the full
   `MergeIntoDevelopRunner` (merge resolves `AlreadyMerged`, push is re-enqueued) until
   origin actually has the work. The existing `accepted-integration-stalled` alert also
   now fires correctly for these cards (it already gates on `status != Integrated`).

**Scope note.** This dev checkout has no access to the live "AgentStudio" production task
board or its `agent-taskboard-stable` log/data directories, so the "before/after pending
count → 0" reconciliation for the live ~7-card backlog is an operational follow-up, not
part of this code change: once deployed, the fixed backstop sweep reconciles that backlog
automatically without a manual migration, because it already re-attempts every
Completed/Archive card with `IsIntegrationRequired` and no historical verification record.

**Regression coverage.** `backend.Tests/AutoPushStrategyTests.cs`
(`MoveToCompleted_WithDevelopLine_LogsSkipInsteadOfAttemptingRedundantMainPush`, plus the
pre-existing `AlwaysImmediate_WithDevelopLine_DoesNotAdvanceMainWithRawCommit` guard test,
unchanged and still green) and `backend.Tests/TaskIntegrationStatusServiceTests.cs`
(`BuildLookup_LocalMergeNotPushedToOrigin_RecordedPushFailure_IsPushBlockedNotSilentlyIntegrated`,
`BuildLookup_LocalMergeNotYetPushed_NoRecordedPushAttempt_StaysIntegrated`,
`BuildLookup_LocalMergePushedToOrigin_IsIntegratedEvenAfterAnOlderRecordedFailure`). All
verified to fail without the corresponding fix and pass with it (117 tests total across
the affected suites, 0 failures).
