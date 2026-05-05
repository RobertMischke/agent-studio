# Analysis Reports

Single source of truth for the contract that turns manual and scheduled meta-analyses ("are we on track?", queue health, docs drift, stale jobs, roadmap alignment, token-spend review, QA status, architecture drift, security posture) into first-class durable reports.

> **Language:** English. See [AGENTS.md](../AGENTS.md#documentation-language).
>
> **Schema home:** field-level rules live in [`docs/schemas/analysis-report.schema.json`](schemas/analysis-report.schema.json). The contract here is the prose; the schema is the validator.
>
> **Related:** the design rationale lives in [ROADMAP.md](../ROADMAP.md#analysis-reports-and-meta-actions), [docs/design-principles.md](design-principles.md#analysis-reports-are-first-class-product-memory), and [docs/architecture-decisions.md](architecture-decisions.md) ADR-0022 (the meta-cycle is one producer of analysis reports) and ADR-0023 (storage pattern).

## 1. Purpose and non-goals

An analysis report is the durable record of a system inspection. It answers a project- or workspace-level question by reading evidence already on disk and on the Agent Message Bus, then writes a Markdown artifact for humans plus an optional structured JSON sidecar for the app.

Purpose:

- Make manual and scheduled meta-analyses first-class product output, not chat-only ephemera.
- Give the project page one named home for queue health, drift, stale jobs, roadmap alignment, security posture, QA status, architecture drift, and token-spend review.
- Reuse the same report shape across producers: a manual button click, a scheduled cron-like cadence, the orchestrator meta-cycle, a supporting agent invocation, and the Layer 3 external system review.
- Let the orchestrator and future Layer 3 consumers query findings without parsing prose.
- Carry references back to the raw evidence (jobs, runs, commits, screenshots, bus messages, runtime events, previous reports) so a reviewer can drill down without the report copying the data.

Non-goals (do not add, even if asked offhandedly):

- **Not a workflow engine.** A report records that an inspection happened. It does not move a job between state lanes, edit `job.json`, or fan out coding work. Routine queue movement remains owned by the runner and `RunOutcomePolicy`.
- **Not a database.** Source of truth is many small Markdown + JSON document pairs on disk. An in-memory projection serves query and aggregation; it never owns the data. No SQL, SQLite, LiteDB, or EF.
- **Not a parallel orchestrator.** A `followUpTaskSuggestion` does not silently create a queued job. Follow-up creation is a deliberate, visible action that goes through the existing task creation entry point (the Task Access Layer once it lands; until then, the existing job-creation path).
- **Not a hidden steering mutator.** A report may suggest a README, AGENTS, ADR, or process update. It must not silently rewrite those files. Steering updates go through normal review.
- **Not a duplicate event store.** The Agent Message Bus remains the event spine. Reports reference bus messages by id; they do not copy raw transcripts or whole event streams.
- **Not a log archive.** Reports point at logs via stable refs (`log-slice:<jobId>:<runIndex>:<lineRange>`); they do not embed gigabytes of CLI output.

## 2. Scopes

Every report carries a typed scope. Five scopes are defined; consumers filter and group on this field.

| Scope | When to use | Storage |
|-------|-------------|---------|
| `Workspace` | Cross-project audits ("how is the whole workspace doing?", system review, multi-project token-spend review). | `<workspace>/logs/analysis/_workspace/<reportId>.md` + `.json` |
| `Project` | Default for "are we on track?", queue health, docs drift, roadmap alignment, security posture, architecture drift, QA status, token-spend review. | `<workspace>/logs/analysis/<project>/<reportId>.md` + `.json` |
| `Task` | Inspections explicitly bound to one job folder, e.g. a security audit of one job's diff. | `<workspace>/logs/analysis/<project>/<reportId>.md` + `.json` (with `scope.jobId` populated) |
| `Run` | Inspections of a single CLI invocation, e.g. a tool-call pattern review of run #3 of job X. | Same as Task; `scope.runIndex` populated. |
| `TimeWindow` | "What happened in the last six hours" reports. | Same as Workspace or Project depending on the question; `scope.timeWindow` populated. |

Task- and Run-scoped reports stay reachable from the project-level Analysis Reports surface; they are not buried inside a single task folder. The job folder may carry a sibling pointer in its `results/` directory when the analysis was triggered by a task review, but the durable record lives under `logs/analysis/`.

## 3. Producers

Five producer kinds cover today's traffic. New producers slot in by extending the enum, not by inventing a new file shape.

- **Manual** (`Trigger = Manual`). The user clicks a project-level action button (roadmap alignment, queue health, docs drift, security posture, etc.) or invokes the same action from the companion app. The button is the contract boundary; the user can see what is being triggered.
- **Scheduled** (`Trigger = Scheduled`). A cron-like cadence configured per project ("daily docs drift", "every 4 hours queue health"). Default off. Scheduling lives in project settings; this contract only covers the report shape.
- **MetaCycle** (`Trigger = MetaCycle`). The orchestrator meta-cycle (Layer 2 1/2; ADR-0022) writes a `MetaCycleReport` and may also write an analysis report when the cycle's findings warrant a wider audit. The two records live side by side: `MetaCycleReport` is the cycle's structured operational decision; the analysis report is the human-facing inspection narrative.
- **SupportingAgent** (`Trigger = SupportingAgent`). A supporting CLI agent (security audit, architecture review, UX/UI council, source-map skill) wrote the report after a user-triggered run. The report references the supporting-agent's bus messages by id.
- **ExternalMonitor** (`Trigger = ExternalMonitor`). The Layer 3 external system review monitor (`scripts/supervisor/run-system-review.sh`) emits one report per pass against stable. Workspace-scoped by default.

Producers are descriptive only. Adding a producer kind does not grant any capability; capability comes from code paths the user already controls.

## 4. Document shape

One report = one Markdown file + one optional JSON sidecar with the same stem.

```
logs/analysis/<project>/<reportId>.md          # human-readable artifact
logs/analysis/<project>/<reportId>.json        # structured sidecar (optional)
```

`reportId` is a ULID or UUID v7 so lexical sort matches creation order. The sidecar's filename is the Markdown filename with `.json` appended in place of `.md`; consumers find the sidecar by direct lookup, not by parsing the Markdown.

### 4.1 Markdown is the human artifact

- The Markdown is what a reviewer reads in the activity log, in the project page's drill-down, in the companion app, and on disk months later.
- Lead with a one-sentence verdict, then the evidence, then the suggested follow-up tasks.
- Reference jobs, runs, commits, screenshots, bus messages, runtime events, and previous reports by their stable ids; do not copy raw logs.
- Markdown remains valid evidence even when the JSON sidecar is missing or malformed. A reader who cannot parse the sidecar must still be able to read the report.

### 4.2 JSON is the app contract

- The schema is [`docs/schemas/analysis-report.schema.json`](schemas/analysis-report.schema.json).
- Required fields lock the surface the UI, the bus, and the system-review monitor read against: `reportId`, `scope`, `producer`, `trigger`, `topic`, `createdAt`, `summary`, `severity`, `parseStatus`, `references`, `followUpTaskSuggestions`, `schemaVersion`.
- Field names are camelCase to match `JsonSerializerDefaults.Web` and the existing schema policy.
- Enums spell PascalCase to match the C# records (consistent with [`docs/schemas/README.md`](schemas/README.md)).

### 4.3 Parse-failure behavior

The Markdown is durable. The JSON sidecar is best-effort. Three states are explicit on the record:

| `parseStatus` | Meaning | UI behavior |
|---------------|---------|-------------|
| `Structured` | Both files exist; the JSON validates against the schema. | Show summary, severity, follow-ups, and drill-down chips. |
| `Unstructured` | Markdown exists; the JSON sidecar is missing. | Show the Markdown verbatim, label the report **Unstructured**, do not promise structured filters. |
| `MalformedJson` | Markdown exists; the JSON sidecar exists but failed to parse or validate. | Same as `Unstructured`, plus surface the parser error so a reviewer can fix the sidecar without re-running the analysis. The Markdown stays visible. |

A failed JSON parse never hides the Markdown. A reviewer can always read the human artifact, attach a manual follow-up, and move on. This rule is the load-bearing one - it is what makes Markdown the durable contract and JSON the additive convenience.

## 5. Follow-up task creation rules

A report may suggest follow-up tasks. The rules are deliberately narrow:

- **Suggestions are typed.** Each suggestion carries `title`, `summary`, `priority`, and an optional `relatedTopic` (queue-health, docs-drift, roadmap-alignment, security, architecture, qa, token-spend, runtime-observability, ux-ui, other).
- **Creation is explicit.** The user (or a producer that has been granted creation rights) calls the existing task-creation entry point. The report lists candidates; it does not enqueue them.
- **Default landing lane is `1-preparation`.** Templated topics whose contract is fixed (e.g. "rescue orphan changes") may land directly in `2-ready`. Reports never bypass the user for anything else.
- **No source-code edits.** Even an accepted follow-up does not let the report modify code. Any code change goes through a normal queued task and a normal CLI run.
- **No lane moves.** A report cannot move existing jobs between lanes. The runner remains the single state-machine authority.
- **Steering updates go through review.** A suggestion that recommends a README, AGENTS, ADR, or skill change creates a documentation task; it does not silently rewrite the document.

When a report queues a follow-up task, the report's `followUpTaskSuggestions[].createdJobId` is filled in so a reader can navigate from the suggestion to the resulting job. Until that field is set, the suggestion is a candidate, not a commitment.

## 6. References

A report's value is in what it points at, not in what it copies. The schema's `references` array carries typed pointers; consumers join on these to drill in.

| Ref kind | Stable id shape | Example | Notes |
|----------|-----------------|---------|-------|
| `Job` | `<project>/<lane>/<jobId>` or just `<jobId>` if scope is unambiguous | `agent-taskboard/3-progress/analysis-report-contract-and-storage` | The job folder identity. |
| `Run` | `<jobId>:<runIndex>` | `analysis-report-contract-and-storage:1` | One CLI invocation. |
| `Commit` | `<repoSlug>@<sha>` | `agent-taskboard@a90ea35` | Always full repo slug + SHA so cross-repo reports stay unambiguous. |
| `Screenshot` | path under `<job>/results/` | `analysis-report-contract-and-storage/results/before.png` | Lifecycle follows [docs/protocol-style.md](protocol-style.md). |
| `BusMessage` | AgentMessage id | `01HXYZ...` | Joined against the bus's per-day JSONL. |
| `RuntimeEvent` | typed event name + id | `lifecycle:run-started:01HXYZ...` | Bridged through the bus today; named here so future runtime-only producers have a contract. |
| `PreviousReport` | analysis-report id | `01HXYZ...` | Lets a follow-up report cite what it built on. |
| `LogSlice` | `<jobId>:<runIndex>:<startLine>-<endLine>` | `analysis-report-contract-and-storage:1:42-58` | Span of `cli-output.log`. The slice is a pointer; the bytes stay in the log. |
| `Doc` | path under the app repository | `ROADMAP.md` | Lets a docs-drift report cite the file it was reading. |

The bus is the event spine. A report that wants to cite a supervisor advisory cites the corresponding bus message id, not a copy of the advisory record. This avoids the failure mode where two timelines drift apart because the report duplicated a third of the bus.

## 7. Storage and retention

### 7.1 Locations

- Per-project reports: `<workspace>/logs/analysis/<project>/<reportId>.md` (and `.json` sidecar).
- Workspace-scoped reports: `<workspace>/logs/analysis/_workspace/<reportId>.md` (and `.json` sidecar).
- The workspace's `<workspace>/logs/analysis/` is owned by the analysis-report layer. External writers that bypass the layer are not visible until the projection is invalidated for that (workspace, project) pair.

`logs/` is the workspace's evidence root, consistent with `logs/meta/<project>/` (supervisor) and `logs/bus/<project>/` (Agent Message Bus). Source code lives in the app repo; evidence lives next to the project.

### 7.2 In-memory projection

The backend reads the directory through the same file-backed in-memory pattern as `SupervisorAdvisoryStore` and the agent-message-bus store (ADR-0023). One projection per (workspace, project) pair; the workspace-scoped variant uses the synthetic project key `_workspace`. Disk is the source of truth; the projection is a view that can always be rebuilt by re-reading the files.

The projection serves:

- `Snapshot(workspace, project)` - all reports for the project, newest last.
- `GetById(workspace, project, reportId)` - one report by id.
- `Where(workspace, project, predicate)` - filter by trigger, scope, severity, time window, parse status.
- `ReadSince(workspace, project, cursor)` - cursor-based tail for streaming consumers (UI auto-refresh, future Layer 3 consumer).

Reports are immutable once written. Mistakes are corrected by a follow-up report, not by editing the original. This matches the bus's append-only contract.

### 7.3 Retention

- Reports are not auto-deleted by the backend. They are evidence; they outlive the run that produced them.
- Disk hygiene is a deliberate operator action. A retention policy (e.g. "keep 90 days" or "keep last 200 per project") may ship later as a project setting; until then, reports persist indefinitely.
- A `Resolved` follow-up suggestion does not delete the report or the suggestion. Status is metadata on the suggestion, not a tombstone.

### 7.4 Migration note

The Task Access Layer (ADR-0024) is in phase 1 (contract only) at the time of writing. The first cut of the analysis-report store reads job folders only via stable refs (path strings or `(project, jobId)` tuples) and does not call `JobScannerService.FindJob` or write to `job.json`. When the Task Access Layer ships its mutation phase, follow-up task creation moves to `ITaskAccess.Create` and the existing job-creation path is removed from this layer in the same commit.

## 8. Comparison to neighbouring records

| Record | Cadence | Producer | Owns | Schema |
|--------|---------|----------|------|--------|
| `AgentMessage` | Per event, continuous | All participants | The event spine | `agent-message.schema.json` |
| `SupervisorAdvisory` | Per-tick, mid-run | Supervisor | Health observations | `supervisor-advisory.schema.json` |
| `SupervisorIntervention` | Rare | Supervisor + user | Pre-emptive control actions | `supervisor-intervention.schema.json` |
| `MetaCycleReport` | Per N completed jobs | Meta-cycle | The cycle's operational decision | `meta-cycle-report.schema.json` |
| `DriftReport` | Per drift analysis | Manual / scheduled | Project-level drift score | `drift-report.schema.json` |
| **`AnalysisReport`** | **Per inspection** | **Manual / scheduled / meta-cycle / supporting-agent / external-monitor** | **Generic inspection narrative** | **`analysis-report.schema.json`** |

`DriftReport` is a specialised analysis report shape; the generic `AnalysisReport` covers everything else. A future cleanup may fold `DriftReport` under `AnalysisReport` with a typed `topic = "drift"`, but the two are kept separate today because the drift-score surface needs typed dimension fields the generic shape does not.

## 9. Implementation pointers

- Schema: [`docs/schemas/analysis-report.schema.json`](schemas/analysis-report.schema.json).
- Schema index: [`docs/schemas/README.md`](schemas/README.md).
- Backend record: `OrchestratorApi.Services.Analysis.AnalysisReport`.
- Backend validator: `OrchestratorApi.Services.Analysis.AnalysisReportValidator`.
- Backend store: `OrchestratorApi.Services.Analysis.AnalysisReportStore` (extends `InMemoryStore<AnalysisReportRecord>`).
- Disk paths: `OrchestratorApi.Services.Analysis.AnalysisReportPaths`.
- Tests: `backend.Tests/AnalysisReportStoreTests.cs`, `backend.Tests/SchemaRoundTripTests.cs`.

## 10. Named producers

Named producers wrap one topic with its own scope-selection, prompt, and parse logic. They share the storage contract above; what they own is the inspection logic.

### 10.1 Roadmap Alignment Review (`topic = "roadmap-alignment"`)

Answers the recurring user question "are we on track?". Compares the active queue (`1-preparation`, `2-ready`, `3-progress`, `4-review`) against `README.md`, `ROADMAP.md`, `AGENTS.md`, ADR titles, design principles, mockup folders, and recent analysis reports.

- Service: `OrchestratorApi.Services.Analysis.RoadmapAlignmentReviewService` - pure scope selection, prompt rendering, and JSON parse fallback. No clock or id concerns.
- Prompt template: [`prompts/runtime/roadmap-alignment-review.md`](../prompts/runtime/roadmap-alignment-review.md). Editable Markdown so wording does not require a recompile.
- Endpoints (under `/api/analysis/{project}/actions/roadmap-alignment`):
  - `GET .../prompt` returns the assembled scope summary plus the rendered prompt. Use this from a CLI agent session or the future inline runner.
  - `POST ...` runs the action. Without `agentResponse` it produces an Unstructured "evidence + prompt" report so the user has a durable record that the inspection was requested. With `agentResponse` it parses the agent's reply (Markdown body plus an optional fenced JSON sidecar) and emits the typed verdict.
- Constraints enforced by the service:
  - Follow-up suggestions land in `1-preparation` only. The agent cannot bypass the user by emitting `targetState = "2-ready"`; the service silently coerces.
  - The action is analysis only. It does not move jobs between lanes, edit `job.json`, or modify source files.
  - Stray lane folders (no `job.json` or malformed `job.json`) are surfaced verbatim so the agent can flag the queue as too dirty to score.
- Tests: `backend.Tests/RoadmapAlignmentReviewServiceTests.cs` covers scope selection (lane filtering, stray detection, doc list), prompt construction (placeholders, hard-constraint wording), JSON parse fallback (Structured / Unstructured / MalformedJson), and report assembly (validation, references, tags).
- UI: the project Analysis Reports surface already exposes a "Roadmap alignment" trigger button (slug `roadmapAlignment`); see [`frontend/src/app/components/project-analysis-reports-section.ts`](../frontend/src/app/components/project-analysis-reports-section.ts). UI wiring to the dedicated `actions/roadmap-alignment` endpoint is a follow-up; the existing placeholder route still works for unstructured triggers.
