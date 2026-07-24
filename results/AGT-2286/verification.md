# AGT-2286 verification

Verified on 2026-07-24 from branch
`runner/agent-runner-01/AGT-2286`.

## Review gap addressed

The newest task grade reviewed only the earlier empty salvage commit and could
not attribute the feature commit or its documentation. The task branch now
contains the complete implementation commit `1de147bb` on top of
`origin/develop`.

The load-bearing prompt contracts are now explicit in
`docs/system/contracts/runtime-prompts.md`, linked from the contracts index,
documentation entry page, and frontend domain map. The contract covers source
precedence, review companions, project override comparison, call telemetry,
version history, dead-prompt semantics, and historical cost boundaries.

## Automated verification

- Backend prompt suite:
  `dotnet test backend.Tests/OrchestratorApi.Tests.csproj --filter "FullyQualifiedName~PromptAdmin|FullyQualifiedName~PromptCallTelemetry|FullyQualifiedName~RuntimePrompt"`
  passed 18 of 18 tests.
- Frontend prompt component suites passed 10 of 10 tests across four files.
- `npm --prefix frontend run build` passed. The existing initial-bundle budget
  warning remains.
- Task-attributed TypeScript files passed ESLint.
- `npm --prefix frontend run lint:scss` passed.
- `npm --prefix frontend run lint:structure` passed.
- The full frontend lint command is not green on the current
  `origin/develop` baseline because
  `studio-shell.component.spec.ts` contains two pre-existing empty `toJSON`
  methods rejected by `@typescript-eslint/no-empty-function`. That file is not
  part of this task's diff.

## Live acceptance proof

`frontend/e2e/project/prompt-registry-observability.spec.ts` passed in Chromium
against an isolated backend started by the Playwright dev-backend fixture.

The test proves:

- the all-prompts review runs and persists sidecars;
- `recurring-output-pattern-review.md` is flagged as a real dead prompt;
- the overview exposes prompt classes, calls, last call, last change, last
  review, cost, and status;
- a project pipeline override is shown with project and step provenance plus
  its difference from the shipped file prompt;
- the detail view exposes last change, last review, runtime calls, version and
  cost data;
- overview and detail render in both dark and light themes.

Screenshots:

- `prompt-registry-overview-dark.png`
- `prompt-registry-overview-light.png`
- `prompt-registry-detail-dark.png`
- `prompt-registry-detail-light.png`
