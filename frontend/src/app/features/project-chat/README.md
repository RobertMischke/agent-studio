# project-chat

Slice D virtualised project chat history + full-text search. Embedded inside the orchestrator-side-sheet.

## Public API

Imports via `from './features/project-chat'`. See [`index.ts`](./index.ts).

**Components**:

- `ProjectChatListComponent` — virtualised chat list (CDK virtual scroll), tail-load + scroll-up paging, search-result switching.
- `ProjectChatRailComponent` — narrow right-rail (~22 px) painted next to the chat that mirrors the conversation as a minimap.

**Types**:

- `ProjectChatTurn` — one chat turn (turn-id + author + ts + body).
- `ProjectChatScrollResponse` — scroll/paging response shape.
- `ProjectChatSearchHit`, `ProjectChatSearchResponse` — full-text search results.
- `ProjectChatTurnResponse` — single-turn fetch.

## Notable

- Backed by `/api/projects/{name}/chat/...` endpoints (separate from the per-job CLI output).
- Long-task budget under 50ms during scroll burst is enforced by the e2e perf spec (`e2e/project-chat-virtual.spec.ts`).
