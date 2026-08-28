# Related

- [[verdict-stuck-in-auto-review]] - a different lane, same shape of failure:
  a step's outcome was recorded but the state machine never surfaced it as
  terminal, so the card looked stuck rather than failed.
- `docs/operations/rebase-merge-and-steering/index.html` (AGT-2662) - the
  decision dossier this fix's `develop`-vs-`main` separation follows; its
  "Develop to main" row already states the lineage guard is a `main`-leg-only
  concept.
- `docs/operations/git/commit-push-doctrine.md` - the doctrine behind the
  legacy per-commit completed-job push (`TaskTransitionService.PushJobCommitsAsync`)
  and why it fails closed on a direct `main` advance in a dual-line project;
  item 8 is the exact rule that makes that path noisy (not broken) for
  dual-line projects.
- `docs/operations/develop-main-promotion.md` - the separate, operator-run
  promotion train; a different mechanism with its own lineage/candidate
  contract, out of scope for this fix.
- Code: [`MergeIntoDevelopRunner.cs`](../../../../backend/Features/Pipeline/MergeIntoDevelopRunner.cs),
  [`ImmediateIntegrationLineagePolicy.cs`](../../../../backend/Features/Pipeline/ImmediateIntegrationLineagePolicy.cs),
  [`AcceptedIntegrationFailurePolicy.cs`](../../../../backend/Features/Pipeline/AcceptedIntegrationFailurePolicy.cs),
  [`TaskIntegrationStatusService.cs`](../../../../backend/Features/Tasks/TaskIntegrationStatusService.cs),
  [`IntegrationPushBackstopHostedService.cs`](../../../../backend/Features/Pipeline/IntegrationPushBackstopHostedService.cs),
  [`AcceptedIntegrationBackstopHostedService.cs`](../../../../backend/Features/Pipeline/AcceptedIntegrationBackstopHostedService.cs).
- Tests: [`MergeIntoDevelopRunnerTests.cs`](../../../../backend.Tests/MergeIntoDevelopRunnerTests.cs),
  [`TaskIntegrationStatusServiceTests.cs`](../../../../backend.Tests/TaskIntegrationStatusServiceTests.cs),
  [`AcceptedIntegrationFailurePolicyTests.cs`](../../../../backend.Tests/AcceptedIntegrationFailurePolicyTests.cs).
