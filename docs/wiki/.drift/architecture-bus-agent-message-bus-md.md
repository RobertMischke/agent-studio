---
page: docs/architecture/bus/agent-message-bus.md
category: architecture/bus
pageType: Markdown
producer: software-architecture-drift
cli: claude
lastEvaluatedUtc: 2026-06-22
scoreBand: Watch
overallScore: 68
---

# Drift metadata — Agent Message Bus contract

Conceptual page-metadata note for `architecture/bus/agent-message-bus.md`.
Read-only analysis; the page itself was not edited.

## Verdict

**Watch.** The contract's *semantics* (participant model, message kinds,
storage shape, id strategy, boundary guards) still match the code and
schemas. Drift is concentrated in **stale source paths** and **contract
coverage that lags the schema / HTTP surface** — the doc describes a
slightly older codebase than the one on disk.

## What still matches (healthy)

- **Participant model** — the eight kinds in §2 match the
  `enum` in `docs/schemas/agent-participant.schema.json` exactly
  (`User, Orchestrator, Supervisor, CodingAgent, SupportingAgent,
  SystemReview, Runtime, External`).
- **Message kinds** — the ten kinds in §3.1 / schema match
  `docs/schemas/agent-message.schema.json` `kind.enum`.
- **Storage layout** — §4 matches `backend/Features/Bus/AgentMessageBusPaths.cs`
  and `implementation-state.md` §4 (`{workspace}/logs/bus/{project|_workspace}/{date}.jsonl`,
  participant `:` → `-` on disk).
- **Boundary guards** (§8) hold: store registration + `busMessageAdded`
  SignalR push confirmed in `backend/Host/Program.cs:305,623`; no
  branch/lane-moving code on the bus path.
- **Supporting-agent wiring** (§9b.4) — both wired topics
  (`roadmap-alignment`, `steering-docs-drift`) exist in
  `backend/Features/Analysis/AnalysisReportEndpoints.cs`, and the named
  tests `RoadmapAlignmentReviewServiceTests.cs` /
  `SteeringDocsSummaryDriftServiceTests.cs` exist.
- **Linked elements** `scripts/supervisor/system-health-check.mjs` and
  `scripts/supervisor/system-review.md` resolve.

## Drift findings

### D1 — Stale source paths after `Services/` → `Features/` reorg (Warn)

The bus implementation moved namespace folders, but the prose still points
at the old layout. Live broken markdown links in the page:

- `agent-message-bus.md` lines 153, 167 →
  `backend/Services/Runner/OrchestratorChatLog.cs`
  (actual: `backend/Features/Runner/OrchestratorChatLog.cs`).
- `agent-message-bus.md` line 263 →
  `backend/Endpoints/AnalysisReportEndpoints.cs`
  (actual: `backend/Features/Analysis/AnalysisReportEndpoints.cs`).

Whole-tree moves (not all are clickable links, but the surrounding prose
names them): `backend/Services/Bus/*` → `backend/Features/Bus/*`,
`backend/Models/AgentBus.cs` → `backend/Shared/Models/AgentBus.cs`,
`backend/Endpoints/BusEndpoints.cs` → `backend/Features/Bus/BusEndpoints.cs`,
`Program.cs` → `backend/Host/Program.cs`.

The sibling living doc `docs/architecture/bus/implementation-state.md` is
**more** stale: every path in its §1 table still reads
`backend/Services/Bus/...`, `backend/Endpoints/...`, `backend/Models/...`.

### D2 — Contract prose lags schema (Warn)

`docs/schemas/agent-message.schema.json` carries two fields the contract
never documents:

- `latency` envelope object (schema lines 150-161).
- `tokens.contextWindow` object (schema lines 134-147).

§6's `tokens` block lists only `{ input, output, cacheRead?, cacheWrite?,
model?, dollars? }` and the page never mentions `latency`. This violates
the page's own §10 rule #1 ("if you change the schema … update Sections
4-6 in the same PR"). `implementation-state.md` §2 marks both fields DONE,
confirming the contract page is the lagging artifact.

### D3 — Undocumented HTTP endpoint (Warn)

§9a states "four read endpoints under `/api/bus`" and lists four.
`backend/Features/Bus/BusEndpoints.cs` exposes a **fifth**:
`GET /api/bus/{project}/token-aggregate` (backed by `BusAggregationCache`).
Documented as DONE in `implementation-state.md` §2 but absent from the
contract's endpoint table.

### D4 — Planned migration artifact not yet present (Info)

§9 Phase C references a one-shot reader under `scripts/bus-backfill/`;
that directory does not exist. Consistent with Phase C being future work,
so this is a wording/expectation note, not a contract violation. Soften to
"planned" or create the folder when Phase C begins.

## Cross-cutting

- **No machine-readable architecture model.** The software-architecture-drift
  producer scanned
  `C:\Projects\agent-taskboard-workspace\projects\agent-taskboard\architecture`
  and found no model instance, so per-element scoring against an
  `architecture-model.schema.json` instance is not possible. The contract
  for such a model exists (`docs/architecture/model.md`,
  `docs/schemas/architecture-model.schema.json`) but no instance has been
  authored. This is the producer's mandated high-severity Architecture
  finding; it is independent of this page's accuracy.

## Recommended follow-ups

1. Path-sync `agent-message-bus.md` and `implementation-state.md` to the
   `backend/Features/*` + `backend/Host/Program.cs` layout (doc-only).
2. Add `latency` and `tokens.contextWindow` to §6; add `/token-aggregate`
   to the §9a endpoint table.
3. Author a machine-readable architecture-model instance so the
   architecture-drift producer can score per-element.

## Page disposition

**Needs edits** (doc-only path + coverage sync) plus one **follow-up task**
(author architecture model). No source/schema/ADR change required.
