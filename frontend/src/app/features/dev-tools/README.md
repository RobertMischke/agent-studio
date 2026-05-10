# dev-tools

Developer-only dialogs surfaced through the gear menu in the header. Off in stable builds; gated by `DevToolsService.flags()`.

## Public API

Imports via `from './features/dev-tools'`. See [`index.ts`](./index.ts).

- `UpdateStableConsoleComponent` — full-screen console for the "Update Stable" pull-and-restart flow.
- `E2ECleanupDialogComponent` — bulk-delete generated e2e jobs by pattern.

## Notable

- The DevTools menu state itself + menu open/close lives in the shell (`devToolsMenuOpen`); this folder owns only the modals it surfaces.
- The dev-tools service (`services/dev-tools.service.ts`) is at the root, not under this feature, because flags are read by surfaces outside the dev-tools modals (e.g. the gear button visibility check).
