# Occurrences

Chronological log. Newest at the top. UTC timestamps. One row per observation.

| When (UTC) | Task / context | Agent / CLI | Affected paths | Notes |
|---|---|---|---|---|
| 2026-08-28T09:00:00Z | Session orchestrator dossier entry (AGT-W39) | Claude Fable 5 | `docs/operations/live-improvement-log/index.html` | Root-caused the overnight stall: 705 claims / 0 acceptances, 570+ `Auto-push skipped ... (completed): lineage-blocked` warnings; opened AGT-2688 to fix the push topology, reconcile the backlog, and stop the "completed but pending forever" read that drove the reclaim loop. |
| 2026-08-27T00:00:00Z – 2026-08-28T08:00:00Z | Overnight unattended run | Runner / accept loop | `MergeIntoDevelopRunner`, `IntegrationPushWorker`, acceptance | Deliveries merged into local `develop` (merge step `Passed`) but the deferred push kept returning `lineage-blocked` before ever reaching `origin`. Acceptance read every one of them as integration-`pending`; the runner re-claimed and redelivered already-completed work all night, burning the token window on a board that looked merely stalled rather than broken. |

## AGT-2688 fix session

| When (UTC) | Task / context | Agent / CLI | Affected paths | Notes |
|---|---|---|---|---|
| 2026-08-28 | AGT-2688 | Claude Sonnet 5 | `backend/Features/Pipeline/MergeIntoDevelopRunner.cs`, `AcceptedIntegrationFailurePolicy.cs`, `backend/Features/Tasks/TaskIntegrationStatusService.cs`, `backend/Shared/Models/TaskProvenance.cs` | Reordered the push so `develop` publishes unconditionally before the main-lineage guard runs; recorded the blocked/rejected push as an honest terminal step; added the `IntegrationPushBlocked` failure code and wired it into `TaskIntegrationStatusService` and the accepted-integration recovery decision so a merge-succeeded-push-blocked card reads `conflict-skipped` (not `pending`) and is never replayed by the backstop. See [protocol.md](./protocol.md) for the exact diff shape and [measures.md](./measures.md) for test evidence. |
