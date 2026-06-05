# cli

CLI catalog + per-CLI session/usage admin surfaces. Covers Copilot / Claude / Codex / Gemini.

## Public API

Imports via `from './features/cli'`. See [`index.ts`](./index.ts).

**Components**:

- `CliAdminPanelComponent` — the CLI-usage hub inside the global Workspace Settings home ("Usage caps" rail section): per-CLI quota caps, the embedded full usage detail, and the per-CLI per-project `CliSessionsPanelComponent`. The status-bar "Usage" button opens this section.
- `CliConsoleComponent` — read-only console viewer for raw CLI output.
- `CliSessionsPanelComponent` — per-CLI per-project session inventory (lazy-loaded from `/api/cli/usage`); emits `openJobDetail` so a session's task-link chip opens the owning task. Embedded by `CliAdminPanelComponent`.

**Types** (lifted from `models/job.model.ts` per ADR-0034):

- `CliModelInfo`, `CliModelCatalog` — model catalogue from `/api/cli/{type}/models`.
- `CopilotModelInfo`, `CopilotModelCatalog` — backwards-compat aliases (the records were Copilot-named before the multi-CLI refactor).
- `CliSessionInfo` — one CLI session row.
- `CliUsageProjectGroup`, `CliUsageSection`, `CliUsageReport` — cross-CLI usage report from `/api/cli/usage`.

## Notable

- CLI types live alongside other CLI surface (admin / console / usage). Job-coupled CLI types (`CliExecution`, `CliSettings`, `CliOutputLine`, `ContinueMode`) stay in `models/job.model.ts` because they participate in the JobInfo graph.
- CLI usage has a single hub: the formerly loose right-edge `CliUsageSheetComponent` sidesheet was retired and its quota glance + session inventory folded into `CliAdminPanelComponent` in the Workspace Settings home. See [`frontend/AGENTS.md`](../../../../AGENTS.md) under "Side-sheet layout contract" for the history note.
