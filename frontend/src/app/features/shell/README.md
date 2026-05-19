# shell

Application shell: status bar, workspace banner, and the workspace-level overlays + their open/close state.

## Public API

Imports via `from './features/shell'`. See [`index.ts`](./index.ts).

**State services**:

- `UiPreferencesService` — `taskNavCollapsed`, `compactCards`, `sideSheetWidth` (persisted to localStorage).
- `WorkspaceOverlaysService` (Cycle 9g) — `tokensOpen` / `screenshotsOpen` / `cliAdminOpen` + URL-hash sync (`#/workspace/tokens`, `#/workspace/screenshots`).

**Components**:

- `StatusBarComponent` — bottom strip: default-CLI picker, default-model per CLI, header-quota donut, usage-hover-panel host.
- `WorkspaceBannerComponent` — top strip: surfaces the latest orchestrator-review decision across active projects ("Orchestrator decided X for Y") for at least 30 s.
- `WorkspaceOverlaysComponent` — renders the three workspace overlays (tokens / screenshots / cli-admin) in one container.

## Notable

- All three right-edge side sheets (orchestrator chat, CLI usage, kanban filter) are flex-flow panels that push the studio-shell, not overlays. The three-piece layout contract — `.app-shell` flex-row-reverse wrapper, caller `:host { width: 0 / open; overflow: hidden; flex: 0 0 auto }`, inner `<app-sidesheet> { width: 100% }` — lives in [`frontend/AGENTS.md`](../../../../AGENTS.md) under "Side-sheet layout contract" and is pinned by `e2e/orchestrator-side-sheet-position.spec.ts`.
- The screenshots overlay emits `openTask` events that bubble up through the shell because navigating to a job is shell-coordinated (the shell owns `selectedJob` updates via `JobSelectionService`).
- Project-shell hash sync is separate (lives in `features/project-detail/state/project-overlays.service.ts`); the workspace hash listener calls both on `hashchange`.
