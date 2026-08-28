---
id: lineage-blocked-integration-push
title: "Diverged develop refuses every later delivery forever, logged as lineage-blocked, and a second writer races main"
status: fixed
first-seen: 2026-08-27T00:00:00Z
last-seen: 2026-08-28T09:00:00Z
severity: blocker
category: runner
tags: [integration, git, develop, main, lineage-blocked, accept-loop, auto-push, two-writers]
affects: [backend/Features/Git/GitService.cs, backend/Features/Pipeline/MergeIntoDevelopRunner.cs, backend/Features/Tasks/TaskTransitionService.cs, backend/Features/Tasks/TaskIntegrationStatusService.cs, backend/Features/Pipeline/IntegrationPushBackstopHostedService.cs]
related-tasks: [AGT-2688, AGT-2683]
---

# lineage-blocked-integration-push

**What.** Overnight 2026-08-27/28 the fleet recorded 705 claims and 0
acceptances. The stable backend log carried 570+ occurrences of `Auto-push
skipped for <JobId> at <Sha> (completed): lineage-blocked` (example card:
AGT-2683). The board looked stalled; it was actually two distinct, provable
git-integration defects burning the token window.

## Topology: two writers, one repository

1. **The correct writer** - `MergeIntoDevelopRunner` (immediate integration
   before Human Review, and again at accept time) merges a delivery's task
   branch into the project's `develop` line and pushes it to `origin/develop`
   via `IntegrationPushWorker`. Within one backend process this path is fully
   serialized (`_mergeGate`, `_pushGate`), and `SynchronizeIntegrationBranch`
   already fetched and fast-forwarded before merging.
2. **A second, uncoordinated writer** - `TaskTransitionService.PushJobCommitsAsync`
   (driven by `CompletedPushWorker` on every move to `6-completed`, and by
   `CompletedPushBackstopHostedService` on a 15-minute sweep of every completed
   job) pushes each raw task commit SHA **directly to `main`**
   (`GitService.PushShaAsync`'s hardcoded default target). This path predates
   the develop-then-main promotion train
   ([docs/operations/develop-main-promotion.md](../../develop-main-promotion.md))
   and is enabled by default (`AutoPushStrategy: always-immediate`) for every
   project, including ones with a `develop` line.

For a project with a `develop` line, `GitService`'s lineage guard
(`ValidateDirectMainAdvance` / `ImmediateIntegrationLineagePolicy`) is
**correct** to refuse writer 2: a raw completed-job commit is essentially never
the exact published `develop` tip, so `main` may not advance to it directly.
But writer 2 fires unconditionally for every completed job regardless of
whether the project has a `develop` line at all, so the refusal is not an
occasional edge case - it is a **guaranteed, permanent rejection repeated for
every completed job on every trigger** (the real-time move-to-completed hook,
plus every 15-minute backstop tick over every completed job still in the scan
window). That is exactly the multiplying mechanism behind "570+" occurrences
from a much smaller number of actually-completed cards.

Separately, and independent of writer 2's `main` noise: `develop` itself can
genuinely diverge from `origin/develop` (two live processes each advancing it,
an operator's out-of-band push, a partially-recovered restart). Before this
fix, `SynchronizeIntegrationBranch` correctly detected the divergence and
refused outright - but nothing in the system ever reconciled it, so every
later delivery for that project failed identically until an operator
manually intervened. The failure was recorded as a generic `error` verdict,
indistinguishable from any other integration failure, so an external
accept-loop could not tell "needs an operator on the integration branch
itself" from "needs another coding round" and kept re-queuing work that could
never succeed.

## Fix

1. **Prevent the second writer.** `TaskTransitionService.PushJobCommitsAsync`
   now returns immediately, without attempting any git call, when the
   project's repository has a `develop` line
   (`GitService.HasDevelopLine`, new public helper). `main` in that model only
   advances via the promotion train or the develop-then-main accept path,
   never via a raw per-commit push. Test:
   `AutoPushStrategyTests.PushJobCommitsAsync_WithDevelopLine_SkipsWithoutAttemptingGit`.
2. **Reconcile before failing.** `GitService.SynchronizeIntegrationBranch` now
   attempts one automatic `git merge --no-ff origin/<branch>` before refusing a
   divergence. A clean merge (the common case - two deliveries touching
   different files) preserves every existing commit SHA (a merge rewrites
   nothing, unlike a rebase) and leaves the branch a fast-forward-able
   descendant of origin; the next push succeeds normally. Only a genuine
   content conflict on the reconciling merge itself is reported diverged, and
   the merge is aborted so the tree is left clean. Tests:
   `GitWorktreePrimitivesTests.SynchronizeIntegrationBranch_DivergedButCleanlyMergeable_AutoReconciles`,
   `GitWorktreePrimitivesTests.SynchronizeIntegrationBranch_DivergedWithRealConflict_AbortsAndReportsDiverged`,
   `MergeIntoDevelopRunnerTests.RunAsync_DevelopDivergedButCleanlyMergeable_MergesAndPushFastForwards`,
   `MergeIntoDevelopRunnerTests.Run_LocalDelivery_DivergedIntegrationBranch_AutoReconcilesThenMerges`.
3. **Honest, distinct terminal state.** A divergence that survives automatic
   reconciliation (a real content conflict on the integration branch itself),
   and a push rejected non-fast-forward after a race, are now recorded with
   their own failure code, `integration-push-blocked`
   (`AcceptedIntegrationFailureCodes.IntegrationPushBlocked`) - never the
   generic `error`, and never the old `environmental` label a push rejection
   used to get (which read "infra blip, ignore it" when the truth was "this
   never clears on its own"). The code is never eligible for the operator
   rebase-recovery action (`RebaseRecoveryAvailable: false`): rebasing a
   delivery's own branch cannot fast-forward a branch the platform itself
   cannot push. `TaskIntegrationStatusService.IsDecidedIntegrationAttempt` and
   `IntegrationPushBackstopHostedService`'s 15-minute push retry both
   recognize the code and stop, instead of replaying the identical doomed
   attempt forever. Tests:
   `AcceptedIntegrationFailurePolicyTests.Classify_IntegrationPushBlocked_IsDistinctFromGenericIntegrationError`,
   `MergeIntoDevelopRunnerTests.RunAsync_DevelopDivergedWithRealConflict_ReportsIntegrationPushBlockedNotPending`,
   `AcceptanceIntegrationRoundTripTests.IntegrationPushBackstop_DecidedPushBlockedVerdict_DoesNotRetry`.
4. **Backlog.** No manual reconciliation script was needed: fix 2 runs inside
   `SynchronizeIntegrationBranch`, which every accept-time and immediate-integration
   call already invokes unconditionally at the start of `MergeIntoDevelopRunner.RunSerializedAsync`,
   before any lineage check. The very next delivery attempt for an affected
   project - whether a fresh card or the existing 15-minute
   `AcceptedIntegrationBackstopHostedService` / `IntegrationPushBackstopHostedService`
   sweeps - re-evaluates current git state from scratch and self-heals any
   backlog card whose divergence is cleanly mergeable (the common case),
   converging the pending count to 0 without an operator step. Only a card
   whose divergence is a genuine content conflict on `develop`/`main` itself
   remains blocked, now visibly and honestly so.

## Verification

`dotnet test backend.Tests/OrchestratorApi.Tests.csproj --filter "FullyQualifiedName~GitWorktreePrimitivesTests|FullyQualifiedName~MergeIntoDevelopRunnerTests|FullyQualifiedName~AutoPushStrategyTests|FullyQualifiedName~AcceptedIntegrationFailurePolicyTests|FullyQualifiedName~AcceptanceIntegrationRoundTripTests"`
passes (142 tests across the five affected files), including the pre-existing
`Run_LocalDelivery_DivergedIntegrationBranch_*` regression test, updated to
assert the new, corrected behaviour (auto-reconcile then merge, rather than
refuse forever).
