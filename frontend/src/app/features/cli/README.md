# cli

CLI catalog + per-CLI session/usage admin surfaces. Covers Copilot / Claude / Codex / Gemini.

## Public API

Imports via `from './features/cli'`. See [`index.ts`](./index.ts).

**Components**:

- `CliAdminPanelComponent` — per-CLI usage caps + admin sections, opened from the dev-tools menu.
- `CliConsoleComponent` — read-only console viewer for raw CLI output.
- `CliUsageSheetComponent` — right-hand sidesheet combining the quota strip + per-CLI per-project session list.

**Types** (lifted from `models/job.model.ts` per ADR-0034):

- `CliModelInfo`, `CliModelCatalog` — model catalogue from `/api/cli/{type}/models`.
- `CopilotModelInfo`, `CopilotModelCatalog` — backwards-compat aliases (the records were Copilot-named before the multi-CLI refactor).
- `CliSessionInfo` — one CLI session row.
- `CliUsageProjectGroup`, `CliUsageSection`, `CliUsageReport` — cross-CLI usage report from `/api/cli/usage`.

## Notable

- CLI types live alongside other CLI surface (admin / console / usage). Job-coupled CLI types (`CliExecution`, `CliSettings`, `CliOutputLine`, `ContinueMode`) stay in `models/job.model.ts` because they participate in the JobInfo graph.
- `CliUsageSheetComponent` follows the shared right-edge side-sheet layout contract (host `:host(.is-open) { width: min(440px, 92vw) }`, flex-row-reverse `.app-shell` parent, inner `<app-sidesheet>` width 100 %). Full description in [`frontend/AGENTS.md`](../../../../AGENTS.md) under "Side-sheet layout contract"; regression coverage in `e2e/orchestrator-side-sheet-position.spec.ts`.
