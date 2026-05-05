# Agent Message Bus

Single source of truth for the contract that lets every agent, supervisor, orchestrator, and runtime component speak into one observable conversation layer. Read this before changing how agents log decisions, before adding a new participant kind, and before wiring a new UI panel that shows agent activity.

> **Language:** English. See [AGENTS.md](../AGENTS.md#documentation-language).
>
> **Schema home:** all field-level rules live next to this doc under [`docs/schemas/`](schemas/README.md). The contract here is the prose; the schemas are the validator.

## 1. Purpose and non-goals

The Agent Message Bus is the product's communication and observability spine. It records who observed, asked, decided, advised, intervened, produced evidence, spent tokens, transitioned state, errored, or pulsed alive. Every cross-cutting structured signal that today gets buried in a CLI log, a SignalR push, or an ad-hoc `.jsonl` becomes a typed `AgentMessage` so the UI, the system-review monitor, and future analysis skills read from one shape.

Purpose:

- Make every meaningful agent signal inspectable from one timeline.
- Give the Project Screen a participant graph: who talked to whom, on which job, in which run.
- Give the system-review monitor (Layer 3) one input format instead of N format-specific parsers.
- Let supporting agents (security audit, UX/UI council, source-map skill) emit structured events without inventing a new file each time.
- Carry token attribution and artifact references so the security-evidence chain becomes a query, not a manual hunt.

Non-goals (do not add, even if asked offhandedly):

- **Not a workflow engine.** A message records that something happened. It does not move a job between state lanes, queue another task, or fan out coding work. Routine outcomes still flow through `RunOutcomePolicy`; emergency primitives still route through `TaskRunnerService.StopJob` / `SetMode`. The bus is what those layers write into; it is not their replacement.
- **Not a parallel orchestrator.** Producing an `intervention` message does not perform the intervention. The supervisor calls the runner, the runner is the single state-machine authority, and the supervisor also writes a bus message so the user can see why.
- **Not branch orchestration, not workspaces, not parallel coding agents within one project.** The hard product boundary in [AGENTS.md](../AGENTS.md#product-goal--non-goals) and [ROADMAP.md](../ROADMAP.md#hard-boundaries) holds. The bus visualises a sequential pipeline; it does not enable a parallel one.
- **Not a database.** Source of truth is many small JSON or JSONL documents on disk. An in-memory projection serves query and aggregation; it never owns the data.
- **Not a chat history store.** User and agent text already live in `logs/cli-output.log` and the orchestrator chat log. The bus carries decisions, observations, and references to those logs. It does not duplicate raw transcripts.

## 2. Participant model

A participant is one actor that may emit messages. Defined by [`agent-participant.schema.json`](schemas/agent-participant.schema.json). Stable id, declared once per workspace, referenced from every `AgentMessage.participantId` so the timeline does not have to re-derive identity per message.

Eight participant kinds:

| Kind | Examples | Scope | Lifecycle authority |
|------|----------|-------|---------------------|
| `User` | `user` | Workspace | Owns prompts and decisions; the bus never speaks on the user's behalf. |
| `Orchestrator` | `orchestrator`, `orchestrator:my-project` | Workspace and per-project | Owns queue movement and the deterministic post-run policy. The bus mirrors its decisions; it does not replace them. |
| `Supervisor` | `supervisor:my-project` | Per-project | Layer 2 health watcher. Writes advisories and (rarely) intervention records. |
| `CodingAgent` | `agent:claude`, `agent:codex` | Per-project, per-job | The active CLI editing the repository. One per project at any time, by hard boundary. |
| `SupportingAgent` | `support:security-audit`, `support:ux-council` | Per-project or per-job | User-triggered meta worker; runs in its own CLI process when the work is non-trivial. Never edits source code on its own. |
| `SystemReview` | `system-review`, `master-of-disaster` | Workspace | Layer 3 stand-alone monitor; reads bus + disk, writes Markdown reports. |
| `Runtime` | `runtime:taskboard`, `runtime:supervisor-host` | Workspace | The application code itself when it emits lifecycle, error, or heartbeat messages. |
| `External` | `relay:companion`, `companion:phone` | Workspace | Outbound integrations (companion app relay, future webhook adapters). |

Participants are descriptive only. Adding a participant declaration does not grant any capability; capability comes from code. The runner does not consult the participant registry to decide who may move state.

Storage: one JSON document per participant under `logs/bus/participants/<id>.json`. The runtime registers built-in participants on first boot; supporting agents register themselves before emitting their first message.

## 3. Message lifecycle

```
emit -> append (atomic) -> in-memory projection updates -> SignalR fan-out -> UI render / system-review read
```

Every message is small (target under 4 KB), append-only, and immutable once written. There is no edit, no delete, no reorder. Mistakes are corrected by emitting a new message that references the wrong one via `replyToId` or `correlationId`.

### 3.1 Producers

- **Runtime** writes `lifecycle` messages on job create, run start, run stop, sentinel parsed, state lane moved, recovery restarted; `error` messages on unrecoverable backend failures; `heartbeat` messages on supervisor host tick.
- **Orchestrator** writes `decision` messages mirroring `OrchestratorChatLog` entries (Decision / Reissue / HeuristicFallback / GiveUp). The chat log remains the textual record the activity-log parser already reads; the bus message is the typed projection.
- **Supervisor** writes `advisory` messages mirroring `SupervisorAdvisory` and `intervention` messages mirroring `SupervisorIntervention`.
- **Coding agent** does not write to the bus directly. The runner observes its CLI output, parses sentinels and tool calls, and emits `lifecycle` and `decision` messages on its behalf. Free-form agent text stays in `cli-output.log` and is referenced via `artifact:log-slice` when a bus message needs to point at a specific span.
- **Supporting agent** writes `observation`, `artifact`, and `decision` messages keyed to its skill. A council member writes one `observation` per critique pass; the council coordinator writes one final `decision`.
- **User** writes `question` messages when the UI sends a prompt or follow-up. The runtime is the actual writer; `participantId` is `user`.

### 3.2 Consumers

- **Frontend project view**: subscribes to a per-project SignalR stream and renders the timeline + participant graph.
- **System-review monitor (Layer 3)**: tail-reads `logs/bus/<project>/<date>.jsonl`, joins on `runId`, and writes a Markdown health report.
- **Companion app relay**: serves the most recent N messages per project to the phone PWA, filtered to `decision`, `intervention`, and `question` kinds for the small surface.
- **Backend in-memory store**: keeps the last K messages per project hot for `/api/projects/{id}/bus` queries; older messages stay on disk and are paged in lazily.

### 3.3 Ordering

Lexical order of `id` matches creation order when `id` is a ULID or UUID v7. Consumers sort by `id` for stable replay. `createdAt` is the wall-clock truth but may collide; `id` is the tie-breaker. The bus does not promise causal ordering across participants; use `replyToId` and `correlationId` for causality.

## 4. Storage shape

Source of truth: many small JSON or JSONL documents on disk. No SQL, no SQLite, no LiteDB, no EF. The repo's [Schema-First Communication and In-Memory Data Layer](../ROADMAP.md#schema-first-communication-and-in-memory-data-layer) doctrine applies here in full.

Layout under each watched workspace:

```
<workspace>/
  logs/
    bus/
      participants/
        user.json
        orchestrator.json
        supervisor-my-project.json
        agent-claude.json
        ...
      _workspace/
        2026-05-05.jsonl
        2026-05-06.jsonl
      <project>/
        2026-05-05.jsonl
        2026-05-06.jsonl
```

Rules:

- One JSONL file per participant scope per UTC day. Daily rotation keeps individual files under a few megabytes even on busy projects.
- One message per line. Strict UTF-8. No trailing comma, no enclosing array. Lines are independently parseable.
- Messages with `project: null` go to `_workspace/`. Messages with a project go to `<project>/`. Cross-project correlation uses `correlationId`, not file movement.
- Append-only. The writer opens the file in append mode, writes a single `JSON.stringify(message) + "\n"`, fsyncs on Windows-default cadence, and closes. No partial writes; if the JSON serialiser throws, nothing is written.
- File names are the source of `project` and `date` filters; the in-memory projection indexes by `(project, date, participantId, kind)` only.
- Old days are not deleted by the runtime. A future workspace-hygiene job may roll them into monthly archives; that is not part of v1.
- Participant files are small JSON documents (not JSONL) so the registry can be loaded with one parse per participant on boot.

The bus runs alongside, not on top of, the existing `logs/meta/<project>/observations.jsonl` and `interventions.jsonl`. During the migration window (see Section 9) the supervisor writes both. After migration the supervisor writes the bus only and a one-shot reader translates the legacy files for system-review backfill.

## 5. Id and correlation strategy

Five identifier fields, each with a narrow purpose. Do not overload them.

| Field | Format | Purpose |
|-------|--------|---------|
| `id` | ULID or UUID v7 | Globally unique, lexically sortable. The canonical reference target. |
| `replyToId` | Another `id` | Direct answer relationship. Question -> answer, decision -> follow-up decision, advisory -> intervention. Render as a thread in the UI. |
| `correlationId` | Free-form, often the originating user message `id` | Group of messages that belong to one logical activity but cross participants and runs. Render as a participant graph cluster. |
| `runId` | Stable id from `/api/jobs/{id}/runs` | Bus message belongs to one CLI invocation. Lets the timeline filter "show me run 3" without scanning text. |
| `cliSessionId` | Provider session id | Bus message belongs to one provider chat session. Lets system-review correlate with on-disk session evidence. |

Recommended id choice: ULID. 26 chars, lexically sortable, no dashes, OS-clock based. UUID v7 is acceptable when a library already produces them. Plain UUID v4 is **not** acceptable because lexical order does not match time and replay becomes O(n log n) on every read.

Correlation rule of thumb: when in doubt, set `correlationId` to the `id` of the user message or lifecycle:JobCreated message that started the activity. The supervisor and supporting agents inherit it on every message they emit during that activity.

## 6. Reference fields: project, job, run, CLI session, artifact, token

Every cross-stream link is a typed field on the message envelope. The bus does not embed copies of foreign records.

- **`project`** matches `ProjectRunnerStatus.projectName` and the watch-paths name. Workspace-wide messages set it to `null`.
- **`jobId`** is the `JobInfo.Id` slug. Lifecycle messages that create the job carry `jobId` (the new id) plus a `payload.transitionReason` describing why.
- **`runId`** is the run id used by `/api/jobs/{id}/runs`. The runner emits it on `lifecycle:RunStarted`; downstream messages within the same run inherit it. When a run dies and is recovered, the recovered run gets a new `runId` and the recovery `decision` carries `replyToId` to the original run's last message.
- **`cliSessionId`** is the provider session UUID (Claude), session id (Codex), session name (Copilot, Gemini). Optional; only set when the runner knows it. Useful for stale-session diagnostics.
- **`artifacts`** is an array of [`agent-artifact-ref`](schemas/agent-artifact-ref.schema.json) pointers. Always references, never inlined bytes. Screenshots live under `<job>/results/`; log slices reference `logs/cli-output.log` with a `byteRange` or `lineRange`; supervisor records reference `logs/meta/<project>/observations.jsonl` with a `lineRange`. Artifact `kind` drives how the UI renders the link.
- **`tokens`** is a per-message attribution block: `{ input, output, cacheRead?, cacheWrite?, model?, dollars? }`. `kind:token-usage` messages always carry it; other kinds may carry it for traceability. Rolling windowed totals belong in [`token-aggregate.schema.json`](schemas/token-aggregate.schema.json), not on each message.

## 7. UI projection expectations

The Project Screen Observability surface (queued at `agent-taskboard/2-ready/project-observability-message-bus-panel/`) renders the bus through three views:

1. **Timeline.** Vertical list of messages, newest at top by default. One row per message. Columns: time, participant glyph + display name, kind chip, severity dot when not Info, summary, optional artifact-count badge. Click a row to open the raw JSON drawer. Heartbeats collapse into a single "alive: N" row per participant per minute. Use `participant-glyph` plus `kind-chip` so the user can scan without reading text.
2. **Participant graph.** Force-directed layout where each node is a participant and each edge is a `replyToId` or shared `correlationId` link, weighted by message count. Hovering a node filters the timeline to that participant. Hovering an edge highlights the messages that connect them.
3. **Aggregates.** Heatmaps and counters: messages per kind per hour, intervention rate per project per day, expensive `token-usage` events ranked by dollars or output tokens, error bursts, long silent periods (no message for > N minutes on a project that has an active run).

Filters available everywhere: participant, kind, severity, time window, jobId, runId, skill (via `tags`), CLI (via `participant.cli`).

Drill-down is mandatory: every aggregate row, timeline row, and graph node has a "view raw" affordance that opens the underlying JSON message and any referenced artifact. Findings without drill-down are findings the reviewer cannot trust.

The frontend already renders `Orchestrator` and `Supervisor` as distinct activity-log participants today (see [`OrchestratorChatLog`](../backend/Services/Runner/OrchestratorChatLog.cs)). The new panel sits beside the activity log, fed by the bus rather than by the parsed `cli-output.log`. Both surfaces stay during the migration window.

## 8. How this preserves the product boundary

Three explicit guards:

1. **Producing a message never moves a job.** The bus is observability and reference; lane transitions remain owned by the runner. The supervisor's `intervention` message records that an intervention was invoked; the actual call is `TaskRunnerService.StopJob` or `SetMode`, unchanged.
2. **One coding-agent participant per project at any time.** The participant model can describe many supporting agents per project, but at most one `CodingAgent` participant has a live `runId` per project. Adding a second one would require relaxing the hard boundary in AGENTS.md and ROADMAP, which is out of scope.
3. **No branch orchestration on the bus.** There is no `kind:branch-create`, no `payload.targetBranch`, no `participant.kind: BranchManager`. If a future feature needs to record git operations, it does so under existing `lifecycle` messages with `payload` fields, not by introducing branch concepts to the bus.

## 9. Migration path from existing logs

Existing structured streams on disk today:

- `logs/cli-output.log` per job (orchestrator chat lines + agent stdout, see [`OrchestratorChatLog`](../backend/Services/Runner/OrchestratorChatLog.cs)).
- `logs/meta/<project>/observations.jsonl` (supervisor advisories).
- `logs/meta/<project>/interventions.jsonl` (supervisor interventions).
- `logs/tokens/<project>.jsonl` (token aggregates, planned).
- `status.md` per job (regenerated, projection only).

Three-phase migration:

**Phase A - bridge writers.** Add bus emit calls beside each existing writer. The supervisor writes the existing JSONL plus a bus message. The orchestrator chat log writes both `cli-output.log` and a bus message. No reader changes; the bus accumulates a complete duplicate of structured signals while consumers stay on the legacy paths. This is the slice in `agent-taskboard/2-ready/bridge-existing-events-to-message-bus/`.

**Phase B - move readers.** New surfaces (Project Screen Observability panel, system-review monitor) read the bus. Legacy surfaces (frontend activity log, supervisor protocol panel) keep reading their original sources. Both stay green.

**Phase C - drop duplicate writers.** Once readers are stable and a workspace-hygiene pass has converted historical legacy files into bus form, the supervisor writes only the bus. The orchestrator chat log keeps writing `cli-output.log` because the activity-log parser is the user-facing transcript and the bus does not duplicate raw transcripts. The bus message for an orchestrator decision references the corresponding `cli-output.log` slice via an `artifact:log-slice`.

A one-shot reader under `scripts/bus-backfill/` translates pre-migration `observations.jsonl`, `interventions.jsonl`, and orchestrator chat lines into bus messages so the system-review monitor can answer questions about runs that predate Phase A. The backfill is idempotent on `id` (deterministic id derivation from source file + line) and never deletes the originals.

No phase requires editing `RunOutcomePolicy`, the supervisor decision logic, or the runner state machine. The bus piggybacks on existing signals; it does not change them.

## 10. Changing this contract

Before you touch any of the moving parts:

1. If you change the **schema** for `AgentMessage`, `AgentParticipant`, or `AgentArtifactRef`, update Sections 4-6 of this file in the same PR and bump `schemaVersion` only when the change forces readers to fork.
2. If you add a **new message kind**, add the enum value to `agent-message.schema.json`, document its expected `payload` and `severity` defaults in Section 3.1, and add a UI glyph in the projection so the timeline is not blind to it.
3. If you add a **new participant kind**, add the enum value to `agent-participant.schema.json`, declare which scopes it lives at in Section 2, and confirm Section 8's "preserves the boundary" guards still hold.
4. If you add a **new artifact kind**, add the enum value to `agent-artifact-ref.schema.json` and a UI render branch in the projection panel.
5. If you change the **on-disk layout**, update Section 4 and add a backfill step in Section 9. The legacy layout must keep working until system-review can read the new one.

The single-source-of-truth rule from [AGENTS.md](../AGENTS.md): if the doc and the code disagree, the doc is wrong. Fix it.
