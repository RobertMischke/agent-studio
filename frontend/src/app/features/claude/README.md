# claude

Type definitions for live Claude session telemetry. Pure types — no components, no services in this folder.

## Public API

Imports via `from './features/claude'`. See [`index.ts`](./index.ts).

- `ClaudeSessionInfo` — per-session token totals + last-update timestamp.
- `ClaudeRateLimitSnapshot` — last `rate_limit_event` frame from the live process (per-turn quota window).
- `ClaudeSessionResponse` — merged shape returned by `/api/jobs/{id}/claude-session`.

## Where the live polling lives

The polling itself is in `features/polling/services/claude-session-poll.service.ts` (a `JobBackgroundPoller<ClaudeSessionResponse | null>` subclass). That service skips non-claude jobs via its `shouldPoll(info)` override.
