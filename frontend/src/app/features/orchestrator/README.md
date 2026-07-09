# orchestrator

Per-project orchestrator: log feed, long-lived session, and the orchestrator chat side sheet (where the user talks to the orchestrator agent directly).

## Public API

Imports via `from './features/orchestrator'`. See [`index.ts`](./index.ts).

**Components**:

- `OrchestratorFeedComponent` — per-project log + token rollup + global card; renders inside an overlay opened from the project tab feed icon.
- `GlobalOrchestratorCardComponent` — shows the singleton orchestrator session above the per-project log.
- `OrchestratorSideSheetComponent` — right-hand sidesheet that hosts the orchestrator chat and project picker. Settings open in the dedicated Orchestrator Settings modal from the sheet header.
- `OrchestratorContextHeaderComponent` — the "where am I right now" locator pinned at the top of the sheet content: project · task (key + title) · lane/state pill · live-run telemetry (model + ticking duration). Data-only so it can be reused verbatim by the task-focused orchestrator surface (separate planning task). The host resolves the run in scope (`App.orchSideSheetActiveRun`): the open task's run, or the running task in the active project when on the board.

**Types**:

- `OrchestratorLogEntry`, `OrchestratorTokenUsage`, `OrchestratorLogResponse` — log feed.
- `OrchestratorSession`, `OrchestratorSessionResponse` — long-lived session (manager-style conversation alongside agent runs).
- `OrchestratorChatTurn`, `OrchestratorChatAttachment`, `OrchestratorChatResponse` — chat surface.

## Notable

- The orchestrator-side-sheet hosts the Composer chat surface from `coding-agent-chat/composer`. The old app-local `features/project-chat` Slice D list and rail were retired during MC-0a; virtualised history and FTS affordances now give way to the library Composer so the app stays a host rather than a second chat implementation.
- The sheet's open/close push contract (host `:host(.is-open) { width: min(640px, 96vw) }` + flex-row-reverse `.app-shell` parent + `<app-sidesheet>` inner-width 100 %) is described in [`frontend/AGENTS.md`](../../../../AGENTS.md) under "Side-sheet layout contract" and pinned by `e2e/orchestrator-side-sheet-position.spec.ts`. Don't introduce `position: fixed` on the host or a fixed px width on the inner `.sidesheet`.
