# session-events

Per-job session-event log: one row per `start` / `continue` / `recovery` event in `logs/session-events.jsonl`.

## Public API

Imports via `from './features/session-events'`. See [`index.ts`](./index.ts).

Pure types only:

- `SessionEvent` — one row (ts + kind + cli + input/captured session ids + resumed flag + optional reason).
- `SessionEventsResponse` — list + ordered `sessionChain[]` (where `'(recovery)'` marks a chain break).

## Where the consumers live

- Polling: `features/polling/services/session-events-poll.service.ts` (10 s).
- The "session continued / lost" chip in the protocol-pane header reads `chainSegmentCount` + `chainLength` from that service.
