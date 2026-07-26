# AGT-2286 verification

Verified on 2026-07-26 from branch
`runner/agent-runner-01/AGT-2286`, rebuilt on `origin/develop` at `9ceb62de6`.

## Branch and review input

The salvaged task commits were rebased onto the current development head and
adapted to the current `PromptPipelineBindings`, runtime prompt catalogue, and
frontend navigation. The final scope diff retains the two operations documents
introduced by the current development head.

No `code-review-grade-*.md` artifact exists in this worktree or its task
results. The recut card and the current product code therefore served as the
review input. The implementation, contract, tests, and evidence are carried by
the card-scoped commits immediately above the development head.

## Automated verification

- Backend prompt administration and telemetry suite:
  `dotnet test backend.Tests/OrchestratorApi.Tests.csproj --no-restore --filter
  'FullyQualifiedName~PromptAdmin|FullyQualifiedName~PromptCallTelemetry'`
  passed 20 of 20 tests.
- The wider prompt and prompt-consumer selection passed 118 tests. One
  pre-existing development-head contract test fails because
  `runner-fresh-start.md` and `runner-reissue-change.md` do not contain the
  model-routing-policy reference that the existing test expects. This task does
  not change either prompt.
- Frontend prompt component suites passed 13 of 13 tests across five files.
- `npm --prefix frontend run build` passed. The existing initial-bundle budget
  warning remains.
- TypeScript ESLint and task-scoped Stylelint passed.
- `npm --prefix frontend run lint:scss` and
  `npm --prefix frontend run lint:structure` passed.
- The repository-wide component-size check reports 17 existing violations in
  files outside this task's diff. No prompt registry component is among them.
- `git diff --check` passed.

## Live acceptance proof

`frontend/e2e/project/prompt-registry-observability.spec.ts` passed in Chromium
with the Angular frontend served directly from this task worktree and the
backend managed exclusively by the Playwright dev-backend fixture.

The test proves:

- the all-prompts review runs and persists adjacent review sidecars;
- `recurring-output-pattern-review.md` is flagged as a real dead prompt;
- the overview exposes prompt classes, calls, last call, last change, last
  review, cost, and status, with sortable columns;
- a project pipeline override is shown with project and step provenance plus
  its difference from the shipped file prompt;
- the detail view exposes last change, last review, runtime calls, version
  history, and date-aware theoretical cost;
- overview and detail render in both dark and light themes.

Screenshots:

- `prompt-registry-overview-dark.png`
- `prompt-registry-overview-light.png`
- `prompt-registry-detail-dark.png`
- `prompt-registry-detail-light.png`
