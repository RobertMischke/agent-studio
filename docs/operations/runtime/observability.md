# Product Runtime Observability

Single source of truth for the contract that lets the software the agents are building be inspected while it runs. Read this before adding logging conventions to a watched project, before wiring a runtime capture path, and before adding a project-level Runtime Observability surface.

> **Language:** English. See [AGENTS.md](../../../AGENTS.md#documentation-language).
>
> **Schema home:** the field-level rules live in [`docs/system/schemas/product-runtime-event.schema.json`](../../system/schemas/product-runtime-event.schema.json). This file is the prose contract; the schema is the validator.

## 1. Purpose and non-goals

Product Runtime Observability is the layer that captures what the *built software* does at run time. It is a peer of, not a replacement for, the Agent Message Bus.

- The Agent Message Bus answers: which agent, supervisor, skill, or orchestrator acted, and why.
- Product Runtime Observability answers: what did the built application do when it ran, where did it fail, how fast was it, and which domain events happened.

Purpose:

- Make the software under construction debuggable from the same workbench the agents work in.
- Capture structured logs, error context, and timings during local runs, Playwright runs, backend tests, and orchestrated CLI runs, so review evidence is durable.
- Give project-level analysis skills one input format to read instead of one log shape per project.
- Carry enough correlation to join a runtime event back to the orchestrator job, run, and (where applicable) Agent Message Bus message that triggered it.

Non-goals (do not add, even if asked offhandedly):

- **Not a replacement for the Agent Message Bus.** Bus messages and runtime events are separate streams with separate schemas. The bus may carry an artifact reference of kind `runtime-event` that points at one of these records; it never embeds them, and a runtime event never moves a job between lanes.
- **Not a hard OpenTelemetry dependency.** The first slice is JSONL on disk plus stdout capture. Optional OpenTelemetry, native platform logging, or vendor SDK adapters may follow once the build-bench loop is stable.
- **Not a database.** Source of truth is many small JSON or JSONL documents on disk, in line with the [Schema-First Communication and In-Memory Data Layer](../../../ROADMAP.md#schema-first-communication-and-in-memory-data-layer) doctrine. No SQL, no SQLite, no LiteDB, no EF.
- **Not a workflow engine.** Emitting a runtime event never starts a task, never moves a job, and never triggers an intervention. The user can ask an analysis skill to read these events and produce a report; the report can suggest a follow-up task. The runtime stream itself is read-only output.
- **Not a production-deployment telemetry plane on day one.** The first slice is build-time first. Production hooks come later, on the same schema, when the workbench captures are stable.
- **Not a chat or transcript store.** Free-form CLI text already lives in `logs/cli-output.log`. Runtime events are typed signals from the *built* software, not the agent's prose.

## 2. Build-time first, production-later

Every serious generated or modified application should have enough observability for local testing, debugging, QA, performance probes, and review. Production deployment hooks can come later, but the build bench should already capture what the app says about itself.

The first slice covers the captures the orchestrator and the developer already produce:

- Local backend or service runs started through `api.sh` or an equivalent script.
- Playwright runs, including screenshots, traces, and the structured log lines the test emits.
- Backend test runs (xUnit, Vitest, Jest, pytest) when they emit structured logs.
- Orchestrated CLI runs that exercise the built software end-to-end.
- Browser console capture during Playwright runs and during local dev sessions when the dev tooling is wired to forward console events.

The same schema works for production later: the producer changes, the sinks may change, but the event envelope stays the same. Until production capture is wired, this contract is silent on shipping events out of a deployed binary; it only describes what the workbench reads.

## 3. Relationship to the Agent Message Bus

The two streams are deliberately separate.

| Concern | Agent Message Bus | Product Runtime Observability |
|---------|-------------------|-------------------------------|
| Producer | Orchestrator, supervisor, supporting agents, system review, runtime, user. | The built software itself: backend services, frontends, CLIs, jobs, scripts. |
| Question answered | Who decided, observed, advised, intervened, asked, answered. | What did the running app do, when, how fast, with which outcome. |
| Lifecycle authority | Routine outcomes flow through `RunOutcomePolicy`; supervisor uses runner primitives. | None. The stream is pure output. |
| Schema | [`agent-message.schema.json`](../../system/schemas/agent-message.schema.json) | [`product-runtime-event.schema.json`](../../system/schemas/product-runtime-event.schema.json) |
| Storage | `logs/bus/<project>/<yyyy-mm-dd>.jsonl` | `<job>/logs/runtime/<yyyy-mm-dd>.jsonl` for job-scoped runs, `<project>/logs/runtime/<yyyy-mm-dd>.jsonl` for project-scoped runs. |

The bus and the runtime stream are joined by reference, not by mixing.

- An `AgentMessage` may carry an `AgentArtifactRef` of kind `runtime-event` whose `uri` points at the runtime event file (and optionally a line range). That is how the bus says "this run produced runtime log artifact X".
- A runtime event may carry a `correlationId` that matches an `AgentMessage.correlationId`, and `jobId` / `runId` fields that match the orchestrator's identifiers.
- A runtime event never embeds an `AgentMessage`, and an `AgentMessage` never embeds a runtime event payload.

If you find yourself wanting to copy fields from one schema into the other, that is the smell that says these should stay two streams. Add a reference instead.

## 4. The structured event envelope

Every runtime event is a small JSON document validated by [`product-runtime-event.schema.json`](../../system/schemas/product-runtime-event.schema.json). The required fields are deliberately minimal so producers can adopt the shape without ceremony:

- `schemaVersion` (currently `1`).
- `timestamp` (UTC, ISO 8601, ends in `Z`).
- `level` (`Trace`, `Debug`, `Info`, `Warn`, `Error`, `Fatal`).
- `event` (stable kebab-case name, optionally dot-namespaced).
- `subsystem` (small fixed vocabulary per project: `backend`, `frontend`, `runner`, `ingest`, ...).

Optional fields that the contract recognises:

- `operation` for finer-grained labels inside a subsystem.
- `correlationId`, `traceId`, `spanId` for joining events across components and future OpenTelemetry adapters.
- `project`, `jobId`, `runId` for orchestrator-scoped runs; `taskId` for the application's own work-item id when it has one.
- `duration.ms` and optional `duration.startedAt` for events that wrap a measurable operation.
- `status` (`Ok`, `Failed`, `Cancelled`, `Timeout`, `Skipped`) for completed operations.
- `error` (`type`, `message`, `stack`, `code`, `retryable`) for errors and timeouts.
- `tags` (kebab-case, capped at 16) for filtering.
- `payload` for kind-specific structured fields.

Hard rules:

- Event names are part of the contract. Renames are breaking changes; deprecate first.
- Producers do not put PII or secrets in `payload` without a documented redaction step. The stream is local today, but the same record may be cited by review reports tomorrow.
- Stack traces over a few KB belong in an attached artifact, not inline in the JSONL line.
- Records are append-only and immutable once written. Mistakes are corrected by emitting a new event, not by editing.

## 5. Recommended sinks

The first slice keeps sinks deliberately simple. A producer chooses one or more of:

- **JSONL on disk** as the source of truth. Job-scoped: `<job>/logs/runtime/<yyyy-mm-dd>.jsonl`. Project-scoped: `<project>/logs/runtime/<yyyy-mm-dd>.jsonl`. One event per line.
- **stdout / stderr capture**: emit JSONL on stdout when running under a captured process. The orchestrator's run wrapper already preserves stdout in `logs/cli-output.log`, so stdout-emitted events end up next to the CLI output.
- **Browser console capture**: the Playwright runner forwards `console` events; producers in the frontend may emit `console.info(JSON.stringify(event))` so the same envelope flows through the same capture.
- **Test-run attachments**: Playwright and backend test runners may attach the JSONL file to the run as an artifact so it ends up under `<job>/results/` along with the screenshots.
- **HTTP diagnostics endpoint** when the built application already has a backend: a `GET /api/diagnostics/runtime?since=...` that tails the local JSONL is acceptable for local inspection. It is not required and must not become the only sink.
- **Optional OpenTelemetry, native platform, or vendor SDK adapters** later. They are read-side or write-side adapters that bridge into or out of the same schema; they never replace the JSONL source of truth.

A producer that cannot reach disk (a short script, a one-off probe) may emit to stdout only. The capture step is what turns stdout into a durable stream.

## 6. Task evidence integration

Runtime events are evidence in the same way commits, screenshots, and the protocol summary are evidence:

- Job-scoped runtime events live under `<job>/logs/runtime/`. They are gitignored on the same rule that gates `<job>/logs/`: logs are durable text and stay in place; binaries do not.
- The protocol summary in `status.md` does not duplicate event content. It may reference the latest error or the slowest operation when the analysis skill flags one, in the `## Notes` section.
- The Agent Message Bus references runtime events through `AgentArtifactRef` of kind `runtime-event`. Layer 3 system review may follow that reference and cite the file or a line range.
- Analysis skills that read runtime events write a normal Markdown plus structured JSON report under the project's analysis-reports area, with `topic: "runtime-observability"` (already enumerated in `analysis-report.schema.json`). Findings can become normal queued tasks; the runtime stream itself is never edited by a skill. The portable [`runtime-log-analysis`](../../../.agents/skills/runtime-log-analysis/SKILL.md) skill is the user-triggered analyser; its per-report contract lives at [`.agents/skills/runtime-log-analysis/references/report-contract.md`](../../../.agents/skills/runtime-log-analysis/references/report-contract.md).
- Recovery after a crashed run reads the runtime JSONL the same way it reads `cli-output.log`: tail the file, surface the last error, and give the next run enough context.

## 7. Project-level UI expectations

The project page should grow a Runtime Observability surface that reads structured product events and answers the questions the user actually asks while a feature is being built:

- Recent product events with subsystem, level, event, summary, and a one-click drill-down to the JSON document.
- Error groups: events with `level: Error` or `Fatal`, grouped by `event` plus `error.type`, with counts, last-seen timestamps, and links to the originating run.
- Latency summaries: percentiles per `(subsystem, operation)` over a window, computed from `duration.ms` on `status: Ok` events.
- Counters: events per minute by `event` and `subsystem`, plus a separate band for warnings and errors.
- Domain timelines: a compact stream of named events (`order.placed`, `payment.declined`, `render.first-paint`) with optional `correlationId` grouping so a single user action reads top-to-bottom.
- Drill-down to the underlying JSONL line, the run that produced it, the screenshot or trace attached to the run, and any Agent Message Bus message that referenced it.

The surface follows the design-principles rules: condensed at the top, drill-down always one click away, no stale state, no hidden failures, one signal per fact. It is read-only; it must not silently mutate code, move jobs, or run new tasks.

## 8. Proportionality

This contract is a tool, not a tax. Apply it in proportion to what the software is.

- **A tiny script does not need a telemetry plane.** A 30-line generator that runs once and produces a file should not grow JSONL infrastructure to satisfy this contract. A normal exit code and a printed summary is enough.
- **A pure-refactor or doc-only change does not add new events.** Observability is not a reason to bloat a small change.
- **A new feature or new subsystem in a product-like application should add events for its meaningful behaviour.** That means stable event names for the operations a reviewer would ask about, structured errors on failure paths, and timings on expensive or user-visible paths.
- **Existing instrumentation must be preserved while editing nearby code.** Silent removal of structured logs is a regression even when the surrounding refactor is correct. Move the events, rename them with a deprecation note, but do not delete instrumentation as a side effect of cleanup.

If a reviewer cannot answer "what did the app do during this run?" from the evidence the change produced, the change has an observability gap. If the answer is already obvious from a single printed line in `cli-output.log`, no further events are required.

## 9. Changing this contract

Before you touch any of the moving parts:

1. If you change the **schema** in `docs/system/schemas/product-runtime-event.schema.json`, mirror the change in §4 of this file in the same PR.
2. If you add a new **sink** convention, add a row to §5 and explain why an existing sink is insufficient.
3. If you add a new **task evidence integration**, update §6 and reconcile with [`docs/system/contracts/protocol-style.md`](../../system/contracts/protocol-style.md) so the protocol pane and the screenshot strip stay consistent.
4. If you propose a **production-deployment hook**, do not change this file in the same PR. Land the workbench capture first, then write a separate addendum that references the production producer.

The single-source-of-truth rule from [AGENTS.md](../../../AGENTS.md): if the doc and the code disagree, the doc is wrong. Fix it.
