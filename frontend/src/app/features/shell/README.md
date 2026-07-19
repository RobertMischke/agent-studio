# shell

Application shell: status bar, workspace banner, and the workspace-level overlays + their open/close state.

## Public API

Imports via `from './features/shell'`. See [`index.ts`](./index.ts).

**State services**:

- `UiPreferencesService` — `taskNavCollapsed`, `sideSheetWidth` (persisted to localStorage). The abolished `compactCards` card-density pref was removed (AGT-2035) and its stored key is cleared on boot.
- `WorkspaceOverlaysService` drives one global Workspace-settings home: `settingsOpen` + `section` grouped General / Global / Workspace: `overview` (General); `appearance` / `updates` / `workspaces` / `task-server` / `remote-hosts` / `orchestrator` (Global); `caps` / `working-memory` / `prompts` / `tokens` / `screenshots` (Workspace). URL-hash sync (`#/workspace/settings`, `#/workspace/settings/{caps,prompts,appearance,updates,workspaces,task-server,remote-hosts,orchestrator,working-memory}`, and the legacy `#/workspace/tokens` / `#/workspace/screenshots`). The retired `summary` section is gone; its `#/workspace/summary` / `#/summary` deep-links now resolve to `overview` (migration: no crash). Legacy `tokensOpen` / `screenshotsOpen` / `cliAdminOpen` / `promptAdminOpen` remain as section-derived computeds so older callers and deep-links keep resolving.

**Components**:

- `StatusBarComponent` — bottom strip: default-CLI picker, default-model per CLI, header-quota donut, usage-hover-panel host, and a single "Settings" entry that opens the Workspace-settings home.
- `WorkspaceBannerComponent` — top strip: surfaces the latest orchestrator-review decision across active projects ("Orchestrator decided X for Y") for at least 30 s.
- `WorkspaceOverlaysComponent`: the one consolidated Workspace-settings view (AGT-2035), with a rail and panel grouped General / Global / Workspace. Global sections: Appearance (Theme + activity-bar side), Updates, Workspaces (registry management, moved off the sidebar), Task Server (AGT-1924: the durable task server's connected URL, workspace store, evidence git status, client registry, and management sweeps; UI-first via `features/task-server`), Remote hosts, Orchestrator. Workspace sections: Usage caps (CLI Management with models, completion-contracts explainer, quota caps, and sessions), Working memory (extracted from Usage caps into its own section), System prompts, Token usage (the single usage area with timeline and usage detail, no double display), Visual evidence. Project onboarding and project basics remain project-owned product flows; there is no Project Sources settings page. Each section keeps its legacy outer test id on the active panel so old deep-links and specs still resolve.

## Notable

- Right-edge side sheets such as orchestrator chat and kanban filter are flex-flow panels that push the studio-shell, not overlays. CLI usage is no longer a side sheet; it opens CLI Management in Workspace Settings. The layout contract lives in [`frontend/AGENTS.md`](../../../../AGENTS.md) under "Side-sheet layout contract" and is pinned by `e2e/orchestrator-side-sheet-position.spec.ts`.
- The screenshots overlay emits `openTask` events that bubble up through the shell because navigating to a job is shell-coordinated (the shell owns `selectedJob` updates via `JobSelectionService`).
- Project-shell hash sync is separate (lives in `features/project-detail/state/project-overlays.service.ts`); the workspace hash listener calls both on `hashchange`.
