# Measures

Fix attempts and their status. Status vocabulary: `tried`, `applied`, `works`, `regressed`.

| Status | Date (UTC) | Measure | Owner | Outcome |
|---|---|---|---|---|
| works | 2026-08-28 | `PushIntegrationBranchAsync` pushes `develop` to `origin` unconditionally before evaluating the main-lineage guard; the guard now only gates the trailing `main` push | AGT-2688 | `origin/develop` is no longer starved by an unrelated local `main`/`develop` divergence; see `PushIntegrationBranch_MainLineageBlocked_StillPublishesDevelopToOrigin` |
| works | 2026-08-28 | A blocked push (main-lineage or a genuine remote divergence) is recorded through `RecordPushStep` with a distinct verdict (`lineage-blocked` / `push-blocked`) instead of an early return that left the step unresolved | AGT-2688 | The `post-merge-into-develop-push` step always reaches a terminal state; `IntegrationPushBackstopHostedService` reliably re-drives it |
| works | 2026-08-28 | New `AcceptedIntegrationFailureCodes.IntegrationPushBlocked` code; `TaskIntegrationStatusService.ReadIntegrationFailure` reads the push step (previously merge-step-only) | AGT-2688 | A merged-but-unpublished card reads `IntegrationStatuses.ConflictSkipped` with a typed, operator-visible reason instead of indistinguishable `pending` |
| works | 2026-08-28 | `ResolveAcceptedIntegrationRecovery` returns `Ignore` when the merge already succeeded and only the push is blocked | AGT-2688 | The accepted-integration backstop no longer replays (re-claims) a card whose delivery is already merged; only the lightweight push retries, on `IntegrationPushBackstopHostedService`'s existing cadence |
| works | 2026-08-28 | No separate reconciliation script: the existing 15-minute push backstop already re-drives every push step that never reached `Passed`/`Skipped`, which includes the entire pre-fix backlog (their push step was never recorded at all) | AGT-2688 | Deploying the fix alone drains the backlog on the backstop's normal cadence; pending count converges to 0 without manual intervention |
| recommended, not applied | 2026-08-28 | Skip the legacy per-commit `TaskTransitionService.PushJobCommitsAsync` (`AutoPushStrategy: AlwaysImmediate`, the default) for projects on the develop-then-main pipeline, since it always targets `main` directly and is rejected by design for those projects | AGT-2688 (follow-up) | Would remove the majority of the 570+ `Auto-push skipped ... (completed): lineage-blocked` log volume; deferred because it touches the completed-transition hot path and needs its own project-settings-driven test, not the git-behavior fix this card was scoped to |

## Before / after

- **Before:** pending-integration count for the affected backlog: ~7 cards
  stuck (per the originating incident note), plus every new delivery joining
  the same stuck state as it was accepted - 705 claims / 0 acceptances
  overnight.
- **After (expected on deploy):** the next `IntegrationPushBackstopHostedService`
  tick (15 min cadence) re-drives `PushIntegrationBranchAsync` for the whole
  backlog; each now pushes `develop` unconditionally and, for any card whose
  local `main`/`develop` divergence has since cleared (the common case - it
  is a snapshot of local ref state, not a permanent condition), the push
  succeeds and the card's integration status flips from `pending` to
  `integrated` on the next `TaskIntegrationStatusService` read. Cards left in
  a genuinely diverged state now surface `integration-push-blocked` instead
  of silent `pending`, so they alarm for operator attention rather than
  looping the runner.
