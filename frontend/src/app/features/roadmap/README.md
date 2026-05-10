# roadmap

Roadmap-intake splitter: takes a long, often multi-language dump and returns candidate tasks the user reviews + edits in place. On confirm, materialises one job folder per accepted candidate into `1-preparation`.

## Public API

Imports via `from './features/roadmap'`. See [`index.ts`](./index.ts).

**Component**: `RoadmapIntakePanelComponent` — the two-step "Send to roadmap" surface. Hosted as a tab inside the orchestrator-side-sheet.

**Types**:

- `RoadmapIntakeCandidate` — one candidate task (title + body + kind + suggested order/CLI + rationale).
- `RoadmapIntakeResponse` — the splitter's response (candidates + notes).
- `RoadmapIntakeConfirmResponse` — the "create N jobs" response (created + skipped lists).

## Notable

- Confirm always lands jobs in `1-preparation` (never `2-ready`) so the board still gets a human review pass.
- All draft / preview state lives in the panel itself — no service in this folder.
