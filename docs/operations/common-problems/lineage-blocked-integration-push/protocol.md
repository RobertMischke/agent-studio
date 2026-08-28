# Root-cause protocol

## Topology map (who writes what, and when)

```
Card accepted (Human Review -> Completed)
        |
        v
MergeIntoDevelopRunner.RunAsync / RunSerializedAsync   (synchronous, in-request)
  - merges the delivery into LOCAL develop
  - when the project has both lines, fast-forwards LOCAL main from that
    develop SHA too (PromoteDevelopToMainAsync)
  - records post-merge-into-develop = Passed
  - MaybeEnqueueIntegrationPush -> IntegrationPushQueue (fire-and-forget)
        |
        | minutes may pass; more cards land through the same synchronous
        | path above, each re-advancing LOCAL main/develop independently
        v
IntegrationPushWorker.ProcessAsync                     (async, off the request path)
  - drains IntegrationPushQueue in order
  - calls MergeIntoDevelopRunner.PushIntegrationBranchAsync
        |
        v
PushIntegrationBranchAsync                              <-- the bug lived here
  - BEFORE FIX: asked "is LOCAL main still an ancestor of LOCAL develop
    *right now*" before ever calling `git push origin develop`. Any drift
    introduced by a later card's synchronous merge (above) answered "no" and
    the function returned "lineage-blocked" without touching origin.
  - AFTER FIX: pushes develop to origin unconditionally first, THEN asks the
    same question only to decide whether the trailing main push may proceed.

IntegrationPushBackstopHostedService                    (durability net, 15 min)
  - re-drives PushIntegrationBranchAsync for any push step that never reached
    a terminal Passed/Skipped - this is what silently retried "lineage-blocked"
    every interval, forever, before the fix (nothing ever recorded a terminal
    Failed for it - see below)

TaskIntegrationStatusService.BuildLookup                (read path, per-repo cache)
  - ancestor set = LOCAL develop UNION origin/develop refs
  - ReadIntegrationFailure(job): BEFORE FIX read only the MERGE step
    (post-merge-into-develop, which had legitimately Passed) - a blocked PUSH
    step was invisible here, so a card with no attributed-commit ancestry
    fell through to the generic IntegrationStatuses.Pending reading.
  - AFTER FIX also reads the PUSH step (post-merge-into-develop-push) when the
    merge step reports no failure, and classifies its terminal Failed status
    (lineage-blocked / push-blocked) as ConflictSkipped with FailureCode
    IntegrationPushBlocked - distinct from pending.

TaskTransitionService.PushJobCommitsAsync -> TryPushCommitAsync  (legacy, per-commit)
  - fires on EVERY 6-completed transition when AutoPushStrategy is
    AlwaysImmediate (the project default)
  - pushes the raw completed-job SHA straight to `main`, no branch argument
  - in a dual-line project this is rejected by ValidateDirectMainAdvance /
    DecideDirectMainAdvance essentially every time BY DESIGN (raw SHAs must
    go through the develop-then-main path) -> logs the exact quoted line,
    "Auto-push skipped for {JobId} at {Sha} (completed): lineage-blocked"
  - independent of, and does not affect, origin/develop or acceptance; a
    noisy but mostly harmless second writer of the same "who publishes main"
    question the develop-then-main policy already owns
```

## What the old code did, precisely

`backend/Features/Pipeline/MergeIntoDevelopRunner.cs`, `PushIntegrationBranchAsync`
(pre-fix shape):

```csharp
if (IsReleaseBranch(integrationBranch) && HasDevelopLine(repoRoot))
{
    var decision = ImmediateIntegrationLineagePolicy.Decide(
        integrationBranch, developAvailable: true,
        mainIsAncestorOfDevelop: _git.IsAncestor(repoRoot, integrationBranch, "develop"));
    if (decision.Mode == ImmediateIntegrationLineageMode.Blocked)
        return new GitPushResult(false, approvedSha ?? "", "lineage-blocked", decision.Reason);
        // <-- returns here. git push origin develop is never called.
        //     RecordPushStep is never called either - the step stays
        //     whatever it already was, not a terminal Failed.

    var developPush = await PushIntegrationBranchSerializedAsync(..., "develop", ...);
    if (!developPush.Success) return developPush;
}
return await PushIntegrationBranchSerializedAsync(..., integrationBranch, ...);
```

`ImmediateIntegrationLineagePolicy.Decide` answers one question: may `main`
follow `develop`. It has no opinion on whether `develop` itself is
fast-forward-pushable to `origin` - that is an entirely separate git fact
(`git push origin develop` succeeds or fails on its own remote-tracking
state). Gating the `develop` push behind the answer to the `main` question
was the defect. The architecture docs already say so explicitly -
`docs/operations/rebase-merge-and-steering/index.html` ("Develop to main"
row): *"The promotion train gates a pinned develop candidate and publishes
that exact SHA... Non-fast-forward lineage or a moved gated checkout blocks
publication"* - describing the `main`-only leg, never `develop` publication.

## Verification of the fix

`backend.Tests/MergeIntoDevelopRunnerTests.cs`:

- `PushIntegrationBranch_MainLineageBlocked_StillPublishesDevelopToOrigin` -
  local `main` is advanced with a commit `develop` never carries (main not an
  ancestor of develop -> lineage `Blocked`), while `develop` itself is a
  clean fast-forward against `origin`. Asserts `origin/develop` receives the
  commit (`RemoteSha(remote, "develop") == developSha`) and `origin/main` is
  untouched, and that the push step is recorded `Failed` /
  `lineage-blocked` / `FailureCode = IntegrationPushBlocked` - not silently
  unresolved.
- `PushIntegrationBranch_MainTarget_DoesNotPublishMainWhenDevelopPushFails`
  (existing test, extended) - a genuinely diverged remote `develop` (a
  competing push landed a different commit) still fails the `develop` push
  itself (`remote-rejected`), main is correctly never published, and the
  push step now records the distinct `push-blocked` verdict /
  `IntegrationPushBlocked` code instead of the generic `environmental`
  bucket.

`backend.Tests/TaskIntegrationStatusServiceTests.cs`:

- `BuildLookup_MergePassedButPushBlocked_IsConflictSkippedNotPending` - merge
  step `Passed`, push step `Failed`/`lineage-blocked`. Asserts
  `IntegrationStatuses.ConflictSkipped` with `FailureCode =
  IntegrationPushBlocked` (not `Pending`), and that
  `ResolveAcceptedIntegrationRecovery` returns `Ignore` - the
  accepted-integration backstop must not replay the merge for a card whose
  merge already succeeded and only the push is blocked.

`backend.Tests/AcceptedIntegrationFailurePolicyTests.cs` - matrix coverage for
both the `lineage-blocked` and `push-blocked` verdicts mapping to
`IntegrationPushBlocked`.

All four suites plus the adjacent lineage-policy, backstop, and
`GitWorktreePrimitives` push suites (132 tests total) pass:
`dotnet test backend.Tests/OrchestratorApi.Tests.csproj --filter
"FullyQualifiedName~MergeIntoDevelopRunner|FullyQualifiedName~AcceptedIntegration|FullyQualifiedName~IntegrationPushBackstop|FullyQualifiedName~ImmediateIntegrationLineagePolicy|FullyQualifiedName~TaskIntegrationStatus|FullyQualifiedName~GitWorktreePrimitives"`.

## Scope note: what this fix does not change

The `develop`-vs-`origin` non-fast-forward case (a *real* divergence, not a
`main`-only lineage question) still fails the push - correctly, since a blind
force-push would silently discard someone's commits. That case now reports
the distinct `integration-push-blocked` state instead of `pending`, and
`IntegrationPushBackstopHostedService` keeps retrying it on its normal
cadence until an operator reconciles the actual divergence; it does not
force-push and does not invent a reconciliation it has no authority to
perform.
