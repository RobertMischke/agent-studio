# run-timeline

Per-job CLI-invocation timeline. One run = one CLI invocation between two user inputs (the unit-of-conversation surface documented in `docs/quality/design-principles.md`).

## Public API

Imports via `from './features/run-timeline'`. See [`index.ts`](./index.ts).

Pure types only:

- `RunRecord` — one run (index, ts, lineStart/lineEnd, model, token totals, optional commit).
- `RunTimeline` — the wrapping response (`runs[]` + `hasActiveRun` flag).
- `RunCommitInfo`, `RunCommitsResponse` — commits attributed to a run.
- `RunFileChange`, `RunFilesResponse` — file changes per run.
- `RunDiffResponse` — diff for a chosen file × run.

## Where the live polling lives

`features/polling/services/run-timeline-poll.service.ts` (a `JobBackgroundPoller<RunTimeline | null>` subclass at 5 s cadence). The protocol pane embeds `RunTimelineComponent` + `RunGitViewerComponent` (in `features/job-detail/components/protocol-pane/`) which read this service's signals.
