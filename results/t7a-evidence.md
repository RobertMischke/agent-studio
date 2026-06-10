# T7a evidence: Admin/CLI page (types, models, quota status, completion contracts)

Scope: the workspace Settings "Usage caps" home (rendered by `CliAdminPanelComponent`,
overlay testid `cli-admin-overlay`, title "CLI Management") gains:

- CLI types + default model / thinking, sourced live from `GET /api/cli/{type}/models`.
- Quota LIVE status (pre-existing section, unchanged).
- Completion-contract status per CLI, sourced from real backend adapter mappings via the
  new `GET /api/cli/contracts` (`CliCompletionContract[]`). Claude / Codex / Gemini are
  typed adapters; Copilot is honestly reported as exit-based (no typed adapter).
- A "Working memory" placeholder, labelled coming soon until its backing data lands (T1c).

## Screenshots

- `cli-admin-types-models-contracts--mocked.png` - header + CLI Types & Models (4 cards) +
  Completion Contracts (Claude / Codex / Gemini = TYPED ADAPTER with transport, session
  start, completion, failure and usage-source rows; Copilot = EXIT-BASED).
- `cli-admin-working-memory--mocked.png` - the labelled "coming soon" Working memory section.

## Why these are --mocked, and where the --real shot comes from

A `--real` shot is not reachable from this dev job worktree: the running dev stack
(`:4010` / `:5030`) serves the canonical dev checkout, not this branch, and AGENTS.md
forbids bringing the dev backend up from a job. The `--mocked` shots were produced from
THIS worktree's own production build (`ng build frontend`) served statically on `:4099`
with the CLI admin API surface route-mocked - no supervisor ports were touched.

The `--real` shot is produced by the committed regression spec
`frontend/e2e/cli/cli-admin-models-contracts.spec.ts`, which uses the sanctioned
`dev-backend` fixture and writes `results/cli-admin-types-models-contracts--real.png`
when run from the stable seat. That spec also asserts `GET /api/cli/contracts` returns the
real per-CLI registry (Claude/Codex/Gemini typed, Copilot exit-based).

## Helper artifacts (disposable)

- `_static-server.mjs` - SPA static server for the production build.
- `_mocked-shot.mjs` - Playwright driver that route-mocks the CLI admin API and captures
  the two screenshots above.

## Verification

- `ng build frontend`: green (only the pre-existing initial-bundle budget warning).
- Backend `dotnet test --filter CliCompletionContracts`: 6 passed, 0 failed.
- New frontend CLI specs (`cli-models-panel`, `cli-contracts-panel`): pass (full run log in
  `logs/unit-test-run.txt`; the unrelated git-pane / notification flakiness is pre-existing).
- SCSS lint clean for the new CLI panel styles (sub-token pill padding annotated inline).
