# screenshots

Per-job + workspace-wide visual evidence. Files live under `<job>/results/`; the workspace reel groups by hour bucket.

## Public API

Imports via `from './features/screenshots'`. See [`index.ts`](./index.ts).

**Components**:

- `ScreenshotStripComponent` — per-job strip embedded in the protocol pane.
- `WorkspaceScreenshotsComponent` — full-screen overlay reel (status-bar entry + `#/workspace/screenshots` deep-link).

**Types**: `JobScreenshot`, `JobScreenshotsResponse`, `WorkspaceScreenshotsResponse`.

## Notable

- All `relativePath` values begin with `results/` (relative to the job folder).
- Workspace overlay open/close + URL hash sync lives in `features/shell/state/workspace-overlays.service.ts`.
- Polling: `features/polling/services/screenshots-poll.service.ts` (10 s).
