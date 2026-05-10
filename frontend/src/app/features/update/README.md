# update

Update-service surfaces: version badge, banner (success/failure), full-screen block-modal during in-flight updates, and the update-center drawer.

## Public API

Imports via `from './features/update'`. See [`index.ts`](./index.ts).

**Components**:

- `UpdateVersionBadgeComponent` — header version pill; clickable, opens the update center.
- `UpdateBannerComponent` — top banner with three states: done (with reload button), done-no-change, failed (with rollback + dismiss).
- `UpdateBlockModalComponent` — full-screen click-blocking modal that takes over the UI while an update is in flight; survives F5 because the FE keeps polling.
- `UpdateCenterComponent` — drawer-style overlay with pending updates, history, and trigger buttons.

## Notable

- The `UpdateClientService` (in `services/update.service.ts`) is the API client; this folder owns the four UI surfaces that bind it.
- The 'behind' indicator on the version badge is read-only: it surfaces what `update.service.ts` polls; clicking opens the center where the user can act.
