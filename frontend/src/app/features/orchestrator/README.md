# orchestrator

Per-project orchestrator: log feed, long-lived session, and the orchestrator chat side sheet (where the user talks to the orchestrator agent directly).

## Public API

Imports via `from './features/orchestrator'`. See [`index.ts`](./index.ts).

**Components**:

- `OrchestratorFeedComponent` — per-project log + token rollup + global card; renders inside an overlay opened from the project tab feed icon.
- `GlobalOrchestratorCardComponent` — shows the singleton orchestrator session above the per-project log.
- `OrchestratorSideSheetComponent` — right-hand sidesheet that hosts the orchestrator chat + roadmap-intake tab + project-list tab. The **Logic** tab inside the sheet (`OrchestratorLogicPanelComponent` under `components/orchestrator-logic-panel/`) renders the orchestrator + supervisor flag catalog (review-decision, prep, soft-reasoning, meta-cycle, auto-intervention) and is opened from the dev-tools menu's "Orchestrator config" item.

**Types**:

- `OrchestratorLogEntry`, `OrchestratorTokenUsage`, `OrchestratorLogResponse` — log feed.
- `OrchestratorSession`, `OrchestratorSessionResponse` — long-lived session (manager-style conversation alongside agent runs).
- `OrchestratorChatTurn`, `OrchestratorChatAttachment`, `OrchestratorChatResponse` — chat surface.

## Notable

- The orchestrator-side-sheet (1321 LOC) hosts THREE tabs in one component. Splitting per-tab is a candidate for a future cycle.
- Project-chat (Slice D — virtualised history + FTS search) lives in [`features/project-chat/`](../project-chat/), not here. The orchestrator-side-sheet imports it.
