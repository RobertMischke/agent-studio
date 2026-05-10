# verbose-debug

Debug overlay for the active job: tabs for overview / actors / orchestrator / tools / raw trace / etc. Opened from a header button in the protocol pane (gated by feature flag).

## Public API

Imports via `from './features/verbose-debug'`. See [`index.ts`](./index.ts).

**Component**: `VerboseDebugOverlayComponent` — the overlay itself (~1237 LOC; per-tab subcomponent split is a future cycle).

## Notable

- Read-only: never mutates job state.
- Reads from the running job's polled signals (cliOutput, runTimeline, screenshots, tokenSummary) — it's a renderer over already-fetched data, not a poller.
- Also opens for an arbitrary `verboseDebugContext` shaped at the shell so the user can debug a session that isn't currently the open detail (e.g. via a project-side-sheet "🐞" affordance).
