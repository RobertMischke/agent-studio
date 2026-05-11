# Agent Message Bus — Implementation State (Living Doc)

> **Maintained by agents.** Update this file whenever you change the bus implementation, wire a new bridge, or discover a gap. The formal contract is `agent-message-bus.md`; this file records the *current reality* vs. what the spec promises.
>
> Last updated: 2026-05-11

---

## 1. Implemented (green)

| Component | Location | Status |
|-----------|----------|--------|
| `AgentMessageBusStore` | `backend/Services/Bus/AgentMessageBusStore.cs` | ✅ JSONL append, in-memory projection, atomic per-file semaphore |
| `AgentMessageBusBridge` | `backend/Services/Bus/AgentMessageBusBridge.cs` | ✅ Typed helpers for orchestrator chat, supervisor advisories/interventions, run lifecycle, token-usage, supporting agents |
| `AgentMessageValidator` | `backend/Services/Bus/AgentMessageValidator.cs` | ✅ Validates against known enums (roles, kinds, severities, artifact kinds) |
| `AgentMessageBusPaths` | `backend/Services/Bus/AgentMessageBusPaths.cs` | ✅ Storage path management (`{workspace}/logs/bus/{project}/{date}.jsonl`) |
| `BusEndpoints` | `backend/Endpoints/BusEndpoints.cs` | ✅ GET /api/bus/{project}/summary, /recent, /messages, /messages/{id} |
| `AgentBusService` | `frontend/src/app/services/agent-bus.service.ts` | ✅ HTTP polling wrapper with best-effort fallback |
| `AgentBus.cs` models | `backend/Models/AgentBus.cs` | ✅ Full C# records: AgentMessage, AgentParticipant, AgentArtifactRef, AgentMessageTokens, AgentMessageQuery, AgentMessageSummary |
| Backend tests | `backend.Tests/AgentMessageBusStoreTests.cs`, `AgentMessageBusBridgeTests.cs` | ✅ Contract tests for append, query, bridge mapping |
| Bridge: OrchestratorChatLog | `OrchestratorChatLog.cs` → `AgentMessageBusBridge` | ✅ Decision/Reissue/HeuristicFallback/GiveUp mirrored |
| Bridge: SupervisorAdvisories | `HardHealthCheckHostedService` | ✅ Advisories mirrored |
| Bridge: SupervisorInterventions | `SupervisorInterventionService` | ✅ Interventions mirrored |
| Bridge: Supporting agents | `AgentMessageBusBridge.EmitSupportingAgentReportAsync` | ✅ Roadmap-alignment wired; others planned |
| Token-usage messages | `kind:token-usage` via bridge on orchestrator turns | ✅ `tokens.{input,output,cacheRead,cacheWrite,model}` fields present |
| Storage schema | `docs/schemas/agent-message.schema.json` | ✅ |
| Participant schema | `docs/schemas/agent-participant.schema.json` | ✅ |

---

## 2. Gaps (red) — not yet implemented

| Gap | Impact | Notes |
|-----|--------|-------|
| **No SignalR push for bus events** | UI must poll; no live updates | Documented in spec Section 3.2 but not wired to `JobHub.cs` |
| **No token aggregation endpoint** | Can't quickly sum tokens by model/participant/day | Need `/api/bus/{project}/token-aggregate` or query parameter on /summary |
| **No context-window tracking** | Can't see how large the context was, what was loaded | Needs new `contextWindow` field on `kind:token-usage` messages |
| **No latency tracking** | Can't see TTFB or total response time per turn | Needs new `latency` field on token-usage or a new `kind:latency` |
| **No unified chat aggregation** | Three systems (bus, ProjectChat, OrchestratorChat) not merged into one timeline | Cross-system correlation is manual |
| **No unified TypeScript model** | Frontend has three separate message shapes | Needs adapter/mapper to a common `TimelineEntry` |
| **No real-time bus subscription** | No `/api/bus/{project}/stream` or SSE endpoint | All consumers are polling |
| **No workspace-wide message index** | Can't search "all messages with 'timeout' across all projects" | Per-project chat FTS only |
| **Participant graph not computed** | Schema supports `replyToId`/`correlationId` graph; no service builds it | Planned for Project Screen observability panel |

---

## 3. User Requirements (open — 2026-05-11)

These were explicitly requested by the user and need implementation:

### 3a. Token Usage Aggregation
**Goal:** Quickly aggregate token spend by participant, model, day, project.  
**Approach:**
- Add `/api/bus/{project}/token-aggregate?groupBy=model|participant|day&since=...&until=...` endpoint
- OR add `aggregate` section to existing `/summary` response: `{ byModel: [{model, input, output, cacheRead, dollars}], byParticipant: [...], byDay: [...] }`
- Data is already in `kind:token-usage` messages; just needs a rollup pass over the in-memory projection

**Schema change:** None required — data already present.

### 3b. Context Window Tracking
**Goal:** Know what was in the Claude context window per turn: total size, files loaded, system prompt size, conversation history size.  
**Approach:**
- Add new optional field to `AgentMessageTokens` (and schema):
```json
"contextWindow": {
  "totalSize": 200000,
  "used": 87432,
  "remaining": 112568,
  "systemPromptTokens": 12000,
  "conversationTokens": 71000,
  "filesLoadedCount": 14,
  "largestFiles": ["path/to/file.ts (4200 tok)", "..."]
}
```
- Emit via bridge when runner parses token usage from CLI output (Claude reports context % in output)
- Frontend: show context-usage bar in task detail and bus timeline

**Schema change:** Add `contextWindow` to `agent-message-tokens.schema.json`.

### 3c. Latency Tracking
**Goal:** Know how fast each model responded (TTFB + total), per turn, per model.  
**Approach:**
- Add new optional field to `AgentMessage` envelope:
```json
"latency": {
  "requestedAt": "2026-05-11T13:30:00Z",
  "firstTokenAt": "2026-05-11T13:30:02.3Z",
  "completedAt": "2026-05-11T13:30:15.7Z",
  "ttfbMs": 2300,
  "totalMs": 15700
}
```
- Runner records `requestedAt` when it sends CLI input, `firstTokenAt` on first stdout line, `completedAt` on exit
- Useful for: identifying slow models, detecting rate-limiting delays, capacity planning
- Emit on `kind:token-usage` or as separate `kind:latency` message

**Schema change:** Add `latency` to `agent-message.schema.json` as optional field.

---

## 4. Current storage reality

```
C:\Projects\agent-taskboard-workspace\
  logs\
    bus\
      participants\        ← registered participant JSON files
      agent-taskboard\     ← per-project JSONL by date
        2026-05-10.jsonl
        2026-05-11.jsonl
      _workspace\          ← workspace-wide messages (project=null)
    pickup-failures.jsonl  ← NOT bus; separate failed-pickup log
```

---

## 5. API quick reference

```
GET /api/bus/{project}/summary
  → { totalMessages, firstAt, lastAt, byKind:{}, byParticipant:{}, bySeverity:{} }

GET /api/bus/{project}/recent?limit=100
  → AgentMessage[]  (newest N, oldest-first within window)

GET /api/bus/{project}/messages?jobId=&runId=&participantId=&kind=&severity=&since=&until=&limit=
  → AgentMessage[]  (AND-combined filters)

GET /api/bus/{project}/messages/{id}
  → AgentMessage | 404
```

Frontend service: `AgentBusService` in `frontend/src/app/services/agent-bus.service.ts`  
HTTP polling; no SignalR wiring yet.

---

## 6. What to do next (priority order)

1. **Token aggregation endpoint** — quick win, data already exists, just needs rollup
2. **SignalR `busMessageAdded` event** — wire `JobHub.cs` to push on every `AppendAsync`
3. **Latency field on schema + runner emit** — low-cost schema extension, high value for diagnostics
4. **Context-window field** — depends on CLI output parsing; Claude shows context % in output
5. **Unified timeline model (TypeScript)** — adapter layer over bus + projectChat + orchestratorChat

---

## 7. Change log

| Date | Change | By |
|------|--------|----|
| 2026-05-11 | Initial doc created based on full codebase scan | Claude (claude-sonnet-4-6) |
