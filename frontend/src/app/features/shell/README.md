# shell

Application shell: status bar, workspace banner, and the workspace-level overlays + their open/close state.

## Public API

Imports via `from './features/shell'`. See [`index.ts`](./index.ts).

**State services**:

- `UiPreferencesService` — `taskNavCollapsed`, `compactCards`, `sideSheetWidth` (persisted to localStorage).
- `WorkspaceOverlaysService` — drives one global Workspace-settings home: `settingsOpen` + `section` (`overview` / `caps` / `prompts` / `tokens` / `screenshots` / `summary`) with URL-hash sync (`#/workspace/settings`, `#/workspace/settings/caps`, `#/workspace/settings/prompts`, `#/workspace/tokens`, `#/workspace/screenshots`, `#/workspace/summary`). Legacy `tokensOpen` / `screenshotsOpen` / `summaryOpen` / `cliAdminOpen` remain as section-derived computeds so older callers and deep-links keep resolving.

**Components**:

- `StatusBarComponent` — bottom strip: default-CLI picker, default-model per CLI, header-quota donut, usage-hover-panel host, and a single "Settings" entry that opens the Workspace-settings home.
- `WorkspaceBannerComponent` — top strip: surfaces the latest orchestrator-review decision across active projects ("Orchestrator decided X for Y") for at least 30 s.
- `WorkspaceOverlaysComponent` — the global Workspace-settings home: a rail+panel "Dach" (mirroring project settings) whose sections embed usage caps (CLI admin), system prompts, token timeline, visual evidence, and summary surfaces. Each section keeps its legacy outer test id on the active panel so old deep-links and specs still resolve.

## Notable

- All three right-edge side sheets (orchestrator chat, CLI usage, kanban filter) are flex-flow panels that push the studio-shell, not overlays. The three-piece layout contract — `.app-shell` flex-row-reverse wrapper, caller `:host { width: 0 / open; overflow: hidden; flex: 0 0 auto }`, inner `<app-sidesheet> { width: 100% }` — lives in [`frontend/AGENTS.md`](../../../../AGENTS.md) under "Side-sheet layout contract" and is pinned by `e2e/orchestrator-side-sheet-position.spec.ts`.
- The screenshots overlay emits `openTask` events that bubble up through the shell because navigating to a job is shell-coordinated (the shell owns `selectedJob` updates via `JobSelectionService`).
- Project-shell hash sync is separate (lives in `features/project-detail/state/project-overlays.service.ts`); the workspace hash listener calls both on `hashchange`.
