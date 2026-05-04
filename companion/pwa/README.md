# Companion PWA (placeholder)

This folder is a deliberate placeholder. The PWA is the next deliverable in the companion-app rollout; it is not implemented yet.

When implemented it will be:

- A small Angular 21 standalone app, separate from `frontend/`.
- Read endpoint: `GET <relay>/state`.
- Write endpoints: `POST <relay>/commands`.
- Auth: bearer token entered on first launch and stored in localStorage.
- One dashboard screen (pipeline + tokens + open decisions) plus a decision-answer modal and a new-task form.

The contract it must satisfy lives in [`docs/companion-app-design.md`](../../docs/companion-app-design.md). [ADR-0018](../../docs/architecture-decisions.md) holds the architecture decision.

Until the PWA exists, the relay can be exercised with `curl` and the local processor's `CompanionSyncService` can be enabled to confirm the snapshot side of the wire.
