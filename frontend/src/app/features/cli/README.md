# cli

CLI catalog + per-CLI session/usage admin surfaces. Covers Copilot / Claude / Codex / Gemini.

## Public API

Imports via `from './features/cli'`. See [`index.ts`](./index.ts).

**Components**:

- `CliAdminPanelComponent`: the "CLI Management" section inside the global Workspace Settings home, with discovered CLI types and models, completion contracts, quota caps, full usage detail, sessions, and working memory. The status-bar "Usage" button and legacy CLI-admin events open this section.
- `CliConsoleComponent` — read-only console viewer for raw CLI output.
- `CliSessionsPanelComponent` — per-CLI per-project session inventory (lazy-loaded from `/api/cli/usage`); emits `openJobDetail` so a session's task-link chip opens the owning task. Embedded by `CliAdminPanelComponent`.

**Types** (lifted from `models/job.model.ts` per ADR-0034):

- `CliModelInfo`, `CliModelCatalog` — model catalogue from `/api/cli/{type}/models`.
- `CopilotModelInfo`, `CopilotModelCatalog` — backwards-compat aliases (the records were Copilot-named before the multi-CLI refactor).
- `CliSessionInfo` — one CLI session row.
- `CliUsageProjectGroup`, `CliUsageSection`, `CliUsageReport` — cross-CLI usage report from `/api/cli/usage`.

## Notable

- CLI types live alongside other CLI surface (admin / console / usage). Job-coupled CLI types (`CliExecution`, `CliSettings`, `CliOutputLine`, `ContinueMode`) stay in `models/job.model.ts` because they participate in the JobInfo graph.
- CLI administration and usage have one hub: the formerly loose right-edge `CliUsageSheetComponent` sidesheet was retired and its management surfaces were folded into `CliAdminPanelComponent` in Workspace Settings. See [`frontend/AGENTS.md`](../../../../AGENTS.md) under "Side-sheet layout contract" for the history note.
