# polling

**Capability feature** (cross-cutting / horizontal slice). Hosts the live-data poll services that watch the currently-open job. Consumers from any feature can inject these directly.

## Public API

Imports via `from './features/polling'`. See [`index.ts`](./index.ts).

**Base class** (Cycle 9k):

- `JobBackgroundPoller<TResponse>` — abstract `@Directive()` that owns the timer + job-key change-detection + visibility-aware ticking. Subclasses declare `intervalMs`, `fetch`, `applyResponse`, `clearValue`, optionally `shouldPoll`.

**Services**:

- `ClaudeSessionPollService` — 5 s, claude-only (skips other CLI types via `shouldPoll`). Two signals: `session`, `rateLimit`.
- `RunTimelinePollService` — 5 s. `timeline` signal + `runs` / `hasActiveRun` computeds.
- `SessionEventsPollService` — 10 s. `response` + `latest` / `chainSegmentCount` / `chainLength` derived signals.
- `ScreenshotsPollService` — 10 s. `screenshots` signal.
- `CliOutputPollService` — **not** a JobBackgroundPoller subclass. It has two-buffer dedup (polled + optimistic), buffer caps, dedup against echoed user lines, an elapsed-time ticker, and starts/stops on the runner's execution status, not just on job change. Stays standalone on purpose.

## Notable patterns

- **Visibility-aware ticking**: `setVisibleInterval` (in `utils/visible-interval.ts`) skips the fetch when `document.hidden`, but keeps the timer armed so the next poll fires immediately when the tab returns.
- **Job-key change detection**: services key off `${watchPath}::${id}`. Re-syncing to the same job is a no-op; a different job re-arms the timer + immediately fetches once.
- The 5 s vs 10 s split: 5 s for surfaces that affect the user's perception of liveness (claude tokens, run timeline cards); 10 s for things that only flip on discrete events (screenshots, session-events).
