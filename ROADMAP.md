# Roadmap

Agent Task Processor is a local control layer for keeping coding agents busy without turning a project into an orchestration platform.

The product goal is simple: keep one coding task moving per project, reduce human babysitting, make review easier, and make security review a repeatable, documented project-level habit.

## Product Thesis

Modern coding agents are useful for long-running implementation work, but they still need a steady queue, clear handoffs, and fast human review. Agent Task Processor turns that loop into a local board:

- The human defines and reviews work.
- The board keeps the queue visible.
- The runner starts the next ready task automatically.
- The agent writes evidence back into the task folder.

The thesis is not to invent another coding agent. The app assumes that productized agents such as Codex and Claude Code are already excellent, economically attractive through subscriptions, and available as direct fallback tools in terminals and IDEs. Agent Task Processor adds the layer those agents do not try to own: ordered local queues, lifecycle boundaries, durable evidence, review handoff, and cross-CLI fallback.

An API-native agent runtime may become attractive later if model pricing, provider features, or subscription limits change enough to make it clearly better. Until then, building a custom coding agent loop is intentionally out of scope.

The product should feel like a workbench, not a command center. It should make one project easier to move through a sequence of tasks, then scale that same pattern across several projects.

Security is part of that thesis. Frontier models are becoming strong enough at cyber tasks that the limiting factor shifts toward process: enough token budget, clear scope, repeatable specialist skills, captured evidence, and a review surface that shows what was checked. UK AISI's April 30, 2026 GPT-5.5 cyber evaluation is the reference point for this roadmap direction: models like GPT-5.5 and Mythos-class systems can outperform casual manual review on hard cyber tasks when given sufficient inference budget and tooling, but the result only becomes useful inside a documented workflow. Documentation is the killer feature: a security review should record which model ran, how much token budget was spent, which process or skill was followed, which evidence was produced, and what conclusion a reviewer accepted.

## Current Shape

Today the application provides:

- A .NET backend and Angular PWA for local use.
- Watched project folders with ordered task states.
- One active coding task per project.
- Parallel execution across different watched projects.
- CLI execution for Claude Code, Codex, GitHub Copilot, and Gemini.
- Live task output, protocol summaries, screenshots, and review evidence.
- CLI quota and session visibility where the underlying tools expose enough data.
- Recovery after session loss via job-folder evidence and deterministic continuation planning.
- Early project-level planning tasks for Security and Architecture dimensions.
- Supervisor, system-review, and meta-cycle concepts for recurring "is this on track?" inspection, with Markdown reports and structured JSON contracts where the app needs to parse results.
- An integrated design mockup for project-level Security, Architecture, UX/UI, Test Quality, Token Usage, Audits and Checks, and Skills under `docs/mockups/quality-system/`. This is exploratory, not product behavior yet.

## Roadmap Themes

### Task Access Layer

Today every backend service that touches jobs reads or writes the filesystem directly. Performance regressions surface when the kanban poll rescans disk per request; concurrent writers race; and any future multi-instance or multi-user story is gated on having one place that owns task state.

A separate **Task Access Layer** (`backend/Services/TaskAccess/`) becomes the single point of access for every job operation. It boots once, loads every project's lane folders into a typed in-memory index, watches the filesystem for external changes, and exposes a typed API for find / list / mutate / transition / subscribe. No service, hosted service, or test is allowed to touch job folders directly; everything goes through `ITaskAccess`.

What the layer enables:

- **Performance**: single index, cheap reads, no per-request disk rescans.
- **Cleaner organisation**: `JobScannerService` and `JobMutationService` become thin facades or disappear; the runner is a state machine that calls the layer.
- **Multi-instance and multi-user later**: with one API, multiple clients or multiple task-processor instances can speak the same protocol. The first cut keeps everything in-process; the typed surface keeps the door open for an HTTP-relay split.

Hard rules:

- File on disk stays the source of truth on cold start. The in-memory index is a view.
- No SQL, no LiteDB, no EF. Files plus an index, same convention as the supervisor and message-bus layers.
- Single-state-machine authority moves into this layer. The runner still owns "one running task per project"; the layer enforces it on every mutation.

Phasing is detailed in the queued task `task-access-api-layer-extraction`: ADR + skeleton, in-memory store, mutations and subscribers, consumer migration, default-on with multi-instance preparation.

### Security First

Make security a first-class project dimension, not a one-off task:

- Project-level Security view that shows the latest security review, review date, outcome, evidence, and open risks.
- Markdown-backed security history so reviews are durable, inspectable, and easy for direct CLI agents to read.
- Security audit records that capture model, CLI, token budget or token usage where available, prompt or skill version, process checklist, evidence links, reviewer decision, and follow-up tasks.
- A token-spend timeline for security work so teams can see whether a review was a quick smoke check or a deep multi-million-token investigation.
- Standard security-review skill that can be selected for a task or project review.
- Project-specific security skills for domain assumptions, threat model, sensitive data, authentication, deployment, and known risks.
- A "security readiness" project action that can create a normal task to run or refresh a security review.
- Roadmap linkage from the existing "Projekt Dimensionen Security und Architektur" task into the project view work.
- A cautious modernization story for small internal libraries: regenerate or rewrite when the scope is bounded and review evidence is strong; avoid this default in highly sensitive areas such as PKI, TLS, cryptography, certificate handling, and authentication boundaries.

Security quality depends on model capability, sufficient token budget, the right process, and durable documentation. The app should optimize that loop instead of treating security as a vague label. A security claim without the model, spend, process, evidence, and reviewer decision is not enough.

### Quality System

Turn the quality-system mockup into product behavior carefully, without letting it become a workflow engine:

- Keep the conceptual split from the mockup: Project Audits are project-scope read-only reviews, Task Checks are task-diff read-only reviews, Performance Probes are runtime measurements, and Skills are reusable workflows.
- Do not use "Quality" as a vague top-level bucket until the vocabulary proves itself in the real app. Prefer concrete surfaces: Security on the project page, Task Checks on task review, Probes under project or settings diagnostics, Skills as the reusable workflow catalog.
- Build the first slice around evidence, not enforcement. Task Checks should produce findings and review chips, but should not block the `3-progress -> 4-review` transition in the first version.
- Default high-severity and security-sensitive Task Checks to separate spawned CLI runs. Allow injected checks only for cheap, low-risk self-checks where structured output is not critical.
- Store audit and check definitions as versioned Markdown with frontmatter, but keep runtime outputs in watched project/task folders. Definitions belong to the app library; findings and reports belong next to the project evidence they describe.
- Treat findings as review artifacts that can become normal queued tasks. A "create follow-up task" action is safer than hidden automatic fixing.
- Keep Performance Probes separate from Skills. Probes execute code and measure the running app; Skills are prompt/workflow definitions.
- Promote Security visually ahead of generic Quality. A project with no security baseline should feel unfinished, not merely unconfigured.
- Delay repository-style skill discovery until local installed skills, licenses, and project lookup are boring and reliable. No hidden internet install, no hidden auto-update, no skill execution without an explicit local record.

First implementation order:

1. Security baseline panel and review history on the project page.
2. Task Check definition model plus per-project defaults.
3. Spawned Task Check run after a main task completes, writing structured findings into the job folder.
4. Finding chips in task review and a follow-up-task action.
5. Skills catalog for installed local skills and built-in audit/check definitions.
6. Performance Probe slots after the audit/check loop is stable.

### Creativity And Design

Make "beautiful software" a first-class product outcome, not a lucky side effect of implementation:

- Treat software as product. Features should be useful, but also visually coherent, pleasant to operate, and able to carry a brand or product idea.
- Add creative design loops that can generate, compare, and iterate UI variants before or during implementation. A loop can ask for "the next version" instead of treating the first usable screen as done.
- Maintain design references: screenshots, markdown briefs, images, accepted examples, rejected alternatives, and product/brand notes that can guide later design loops.
- Use screenshots as the core design evidence. Every visual iteration should capture the current state, the proposed variant, and the critique that led to the next step.
- Add council-style critique as a structured review mode: product, visual design, interaction design, frontend engineering, accessibility, and marketing/positioning can each provide a focused opinion before the orchestrator chooses the next move.
- Keep design councils advisory. They may create findings, design briefs, and follow-up tasks, but they must not create parallel coding work inside one project.
- Model design Skills separately from read-only checks: Visual Direction, UI Polish Pass, Screenshot Critique, Copy Tone Pass, Brand Fit Review, Accessibility Design Review, and Product Story Review are reusable workflows that help produce better design decisions.
- Add explicit Testing and QA actions for backend tests, end-to-end tests, tuning tests, coverage, and run history. The user triggers the action; the app stores structured evidence.
- Add source-code perspectives that an LLM or script-backed Skill can generate: lines of code, modules, dependencies, ownership areas, coverage, hotspots, and organization risks.
- Treat Skill output as a report contract: Markdown for humans plus structured JSON for the app. If parsing fails, show the raw report with an unstructured-output warning.
- Put UX/UI, Test Quality, and Token Usage directly on the project page as first-class menu entries. UX/UI collects design references and iteration evidence; Test Quality collects test runs, coverage, tuning results, source maps, and code metrics; Token Usage shows inference spend across jobs and supporting loops.
- Track token spend as a major project signal. Split usage into Job Tokens, Supporting Jobs Tokens, and Orchestrator Tokens, then aggregate by project, job, run, and time window.
- Add token-usage heatmaps for large boards. Each job can be a square; intensity shows token spend, with drill-down into the exact job, supporting runs, orchestrator turns, and timeline position.
- Let the orchestrator steer a design loop explicitly: accept a version, request another version, ask for a harsher critique, or turn council feedback into normal queued implementation tasks.
- Preserve design history in task evidence: screenshot sets, variant notes, council verdicts, chosen direction, rejected alternatives, and follow-up tasks.

First implementation order:

1. UX/UI, Test Quality, and Token Usage project menu surfaces beside Security and Architecture.
2. Design Loop concept and task-evidence shape for screenshot variants, references, QA runs, source maps, and council notes.
3. Design reference library for screenshots, markdown briefs, images, accepted examples, and rejected alternatives.
4. Screenshot comparison panel on the task detail surface.
5. Local design Skill definitions for screenshot critique, UI polish, copy tone, and accessibility design review.
6. Testing and QA run history for backend tests, end-to-end tests, tuning tests, coverage, and code metrics.
7. Source-code map action that visualizes modules, lines of code, ownership areas, and organization concerns.
8. Token Usage surface with totals, category split, heatmap, timeline, expensive-job list, and drill-down.
9. Council review prompt that produces role-separated critique and a final orchestrator recommendation.
10. "Next version" action that creates a follow-up design iteration task from the current screenshots and council notes.
11. Project-level design memory: chosen visual direction, brand notes, UI principles, and examples of accepted screens.

The design-loop, QA, source-metric, and token-usage concepts live in the single integrated mockup under [docs/mockups/quality-system/](docs/mockups/quality-system/). Do not maintain a second sibling mockup for this product direction.

### Project Control

Make each watched project easier to inspect and operate:

- Project detail pages with path, configuration, status, and quick actions.
- Project dimensions for Security and Architecture, with current status plus historical Markdown records.
- Clearer manual start vs. auto-pickup behavior.
- Safer locking once a task has started, so completed or running work does not drift to another project by accident.
- Better visibility into active CLI sessions that may already be working in the same project.
- Repository hygiene for accepted tasks: surface dirty and unpushed work, support prompt commits of accepted task changes and task evidence, and prevent completed work from quietly piling up on disk.

### Expanded Lifecycle Lanes

Separate human intent from orchestrator and AI processing without breaking the sequential per-project execution model. Today a task in `2-ready` means both "the human says this can run" and "the runner may pick it up". That is too coarse once the app starts doing intake checks, duplicate detection, prompt shaping, post-processing, security feedback, QA checks, and orchestrator review.

The board should make these phases visible:

- Human Ready: the user dragged or created a task as ready from their point of view.
- Orchestrator Intake: an AI or orchestrator lane checks whether the task already exists, is understandable, has enough context, has obvious questions, and can be executed safely.
- Task Execution: the main coding CLI works on the core task.
- Orchestrator Post Processing: a different orchestrator, supporting, or review CLI checks the result, runs requested post-processing, creates findings, asks follow-up questions, or triggers explicit QA, security, design, or runtime-observability checks.
- Human Review: the user reviews the final task evidence and accepts, continues, or sends it back.

V1 should probably implement this as virtual lanes or substates on top of the existing folder states, not as an immediate filesystem-contract explosion. For example, `2-ready` can contain Human Ready and Orchestrator Intake substages; `3-progress` can contain Task Execution and Orchestrator Post Processing substages; `4-review` remains the human-facing review lane. The concept task must decide whether substates belong in `job.json`, a sidecar status file, or a new typed lifecycle event stream.

The UI should support lane grouping and collapse. Users should be able to collapse orchestration-only lanes into a slim left-side rail or compact group, while keeping the main human decision lanes visible. The board should preserve scanability when there are many lanes: group headers, counters, active-run badges, CLI badges, post-processing indicators, and a quick way to expand only the lanes that currently need attention.

Hard boundary: expanded lanes do not permit parallel coding work inside one project. Intake and post-processing are sequential phases in the same project pipeline. If a post-processing check uses another CLI, it must be visible as a distinct supporting or orchestrator run and must not edit the same code concurrently with the main task execution run.

First implementation order:

1. Write the lifecycle-lane concept and state model, including virtual lane vs filesystem state tradeoffs.
2. Add Orchestrator Intake after Human Ready, with duplicate, clarity, missing-context, and executable-shape checks.
3. Add Orchestrator Post Processing after Task Execution, with explicit support for different CLI identity and typed findings.
4. Add grouped and collapsible Kanban lanes with active counters and compact left-rail collapsed state.
5. Add migration and compatibility tests so existing job folders keep rendering correctly.

Queued at `agent-taskboard/2-ready/expanded-lifecycle-lanes-concept/`, `agent-taskboard/2-ready/ready-orchestrator-intake-lane/`, `agent-taskboard/2-ready/post-processing-orchestrator-lane/`, `agent-taskboard/2-ready/kanban-lane-grouping-collapse/`, and `agent-taskboard/2-ready/lifecycle-substate-migration-compatibility/`.

### Task Finding And Shape

Make large boards easier to understand:

- Search across titles, prompts, metadata, and relevant task fields.
- Project-level tags with defaults such as Backend, Frontend, UI Improvement, and Bugfix.
- Better ordering interactions with stronger drag feedback and less visible internal bookkeeping.
- Cleaner archive browsing for completed and historical work.

### Roadmap And Intent

Turn a pile of tasks into a useful product view:

- A project roadmap view that groups open tasks by theme.
- Security and Architecture should be recognized as project-level themes, not just tags.
- Automatic intent extraction from task prompts.
- Follow-up prompts such as "what should be next?", "what is duplicated?", or "what should be split?"
- A path from planning output into a new task draft.

### Agent Feedback

Make agent work easier to judge while it is still running:

- Short protocol summaries at the top of the detail view.
- Mid-run status requests where a CLI supports safe intervention.
- Stronger Activity Log parsing across all supported CLIs.
- Better usage, quota, and model feedback, including edge cases such as model-specific limits.

### Stale Session Reliability

Make continuation after idle, stale, lost, or partially-corrupted sessions a first-class reliability target, especially for Claude Code and Codex:

- Treat stale-session continuation as a product-critical path, not a nice-to-have resume feature.
- Prefer structured session evidence on disk (`session-events.jsonl`, `sessionChain`, `cli-output.log`, `status.md`, `prompt-N.md`) over trusting a provider chat to remain semantically intact forever.
- Add a deterministic daily-session probe suite for Claude and Codex: fresh run, resume within minutes, resume after an idle threshold, resume after backend restart, rejected resume target, recovery run with user follow-up, and no-op-after-recovery reissue.
- Track the age of the resumed session and expose it in logs/UI so stale-resume behavior can be diagnosed from evidence.
- Keep Codex and Claude as the reference implementations. Other CLIs inherit the contract only after the two primary paths are stable.

### Deterministic Orchestration

Treat orchestrator-to-CLI communication as a core capability instead of a side-effect of prompt wording. The orchestrator parses CLI output for typed signals, makes deterministic decisions, and speaks for itself in the chat when it does.

- Hard agent signals (`[[TASK_DONE]]`, `[[TASK_BLOCKED:<reason>]]`, `[[TASK_NEEDS_INPUT:<reason>]]`, `[[TASK_NOOP]]`) parsed from CLI output. Authoritative when present.
- A post-run policy that re-issues a follow-up the agent did not honor, instead of accepting the inconsistency. Bounded retry budget; meta message into the chat on every action.
- An `Orchestrator` participant in the activity log so the user sees the system's decisions next to the agent's replies. Heuristic fallback always surfaces a warning.
- Recovery after a session loss carries the user follow-up as the primary instruction, not a footer the agent can ignore.

### Multi-Loop Supervision

Add a meta-orchestrator loop above the per-project job-pickup loop, plus an external system review monitor. Today the app runs two loops (the CLI agent's internal loop and the orchestrator's job-pickup loop). Both decide things the user only sees afterwards. The supervision layer is the watcher above:

- A **per-project supervisor** that observes the orchestrator's runner in real time and asks fixed questions every tick: is the run progressing, is quota close to exhausted, is the agent's current activity aligned with the prompt scope, are findings accumulating that should pause the queue. Cooperative-signal model by default, with a small set of emergency primitives (cancel run, pause pickup, force fail, resume) for clearly broken behaviour. Each intervention is typed, logged, and visible in the activity feed as a separate participant.
- A **meta-cycle** that runs at quiet batch boundaries, after N jobs reach review or when the user manually asks for an inspection. It pauses pickup, compares recent jobs and evidence against the roadmap and health rules, writes a structured report, then resumes, queues a fix, updates stable through the external helper, or escalates.
- A **continuous protocol** per project: append-only structured logs the user can replay to see what the supervisor saw and decided. A panel on the project page shows the live state.
- A **system review monitor**, run from outside on a multi-hour cadence, that produces structured "after ten hours, this is what the system did and this is what looks off" reports. Stand-alone, not part of the app's runtime, so it survives any app failure mode.

Hard boundary: supervision is advice-first, force-rare. The deterministic post-run policy in `RunOutcomePolicy.cs` stays the authoritative path for routine outcomes; the supervisor adds an outer kill-switch and a soft-reasoning second opinion, not a parallel orchestrator.

The full conceptual analysis - loop-to-loop control options, communication contract sketch, execution-model tradeoffs (in-process vs sidecar vs CLI-driven), open conceptual problems, and recommended task spinout - lives in [docs/research/orchestrator-meta-loop-analysis-2026-05-04.md](docs/research/orchestrator-meta-loop-analysis-2026-05-04.md). The recommended first slice is the system review monitor (Layer 3) because it ships value immediately on stable without touching the runtime.

### Analysis Reports and Meta-Actions

Make manual and scheduled analyses first-class product output. The user should be able to ask a project-level question such as "are we on track?", "which jobs are stale?", "does the queue match the roadmap?", "which docs need sync?", "what changed in the last few hours?", or "what should become a follow-up task?" The orchestrator, a supporting agent, the meta-cycle, or an external monitor can answer, but the result lands in the same report system.

The report model:

- Markdown is the durable human-readable report.
- Structured JSON is required when the app needs to aggregate, filter, trend, or create badges from the result.
- Reports carry scope: workspace, project, task, run, time window, source prompt, trigger, producer, schema version, tags, and artifact references.
- Reports can reference Agent Message Bus records, runtime events, screenshots, commits, task folders, test runs, and previous reports.
- If JSON parsing fails, the Markdown remains visible with an unstructured-report warning.
- Findings can create normal queued tasks. Reports do not silently mutate source code or move jobs around the board.

The UI should expose a project-level **Analysis Reports** area:

- Manual action buttons for roadmap alignment, security posture, architecture drift, QA status, token spend review, stale jobs, and docs drift.
- Scheduling controls for daily or every-few-hours analyses, default off.
- Report history with status, severity, producer, scope, time window, parse status, and follow-up links.
- Drill-down to Markdown, structured JSON, raw artifacts, and referenced jobs or bus messages.
- A clear split between project-scoped reports and task-scoped reports. Task reports stay beside task evidence; project reports live at project level.

Queued at `agent-taskboard/2-ready/analysis-report-contract-and-storage/`, `agent-taskboard/2-ready/project-analysis-reports-surface/`, and `agent-taskboard/2-ready/roadmap-alignment-analysis-action/`.

### Agent Message Bus and Observability

Use a central, append-only Agent Message Bus as the product's communication spine. The bus is not a workflow engine and does not relax the one-coding-task-per-project boundary. It is a schema-first event log that records who observed, asked, decided, answered, intervened, spent tokens, and produced evidence.

The useful mental model is "many agents, one visible conversation layer":

- A per-project orchestrator owns queue movement and project context.
- A per-task coding agent is the active Claude, Codex, Copilot, or Gemini run that edits the repository.
- Supporting agents run explicit user-triggered meta work such as QA, security audit, architecture review, source map generation, UX/UI critique, design council feedback, and token analysis.
- Supervisor agents observe health, quota, progress, and stuck loops. The deterministic runner policy remains authoritative for routine outcomes.
- A Layer 3 system health agent, working title "Master of Disaster", periodically reviews the bus and reports whether the whole system looks healthy after hours of work.
- The app runtime also writes messages when it creates jobs, starts runs, stops runs, moves states, parses sentinels, or bridges token usage into the project view.

Message records are small JSON documents, preferably JSONL on disk. The first schema should be `agent-message.schema.json` with stable fields for ids, timestamp, project, optional job/run/session, participant, role, message kind, severity, references, token usage, payload, and schema version. Expected message kinds include `observation`, `question`, `decision`, `advisory`, `intervention`, `artifact`, `token-usage`, `lifecycle`, `error`, and `heartbeat`.

The Project Screen should make the bus visible:

- A communication timeline showing user, task agent, project orchestrator, supporting agents, supervisors, and system review as separate participants.
- A participant graph that answers "who talked to whom" and "which job or run caused this".
- Heatmaps and counters for message volume, intervention rate, expensive token events, error bursts, and long silent periods.
- Drill-down from any aggregate to the raw JSON message, linked job, run, artifact, screenshot, diff, or markdown report.
- Filters by participant, message kind, severity, time window, job, run, skill, and CLI.

Storage stays deliberately simple: many small documents on disk as source of truth, backed by a strongly typed in-memory projection for query, aggregation, and UI speed. No SQL, SQLite, LiteDB, or EF until the file-backed model is proven insufficient. If the bus grows into tens of thousands of documents, the next optimization is indexing and snapshotting inside the in-memory layer, not a premature database migration.

The contract lives in [`docs/agent-message-bus.md`](docs/agent-message-bus.md) with schemas under [`docs/schemas/`](docs/schemas/README.md) (`agent-message`, `agent-participant`, `agent-artifact-ref`). Subsequent slices implement the projection, bridge writers, UI panel, supporting-agent emitters, and system-health reader on top of that contract.

First implementation order:

1. Document the contract in `docs/agent-message-bus.md` and add JSON schemas under `docs/schemas/`.
2. Extend the planned in-memory layer with an Agent Message Bus projection over JSONL on disk.
3. Bridge existing streams into the bus: `cli-output.log`, orchestrator chat messages, supervisor advisories and interventions, lifecycle moves, and token usage summaries.
4. Add the Project Screen Observability surface with timeline, participant graph, filters, and raw JSON drill-down.
5. Teach supporting agents and project-level skill actions to emit bus messages and token usage records.
6. Let the Layer 3 system health agent read the bus and produce health reports from the same evidence the user can inspect.
7. Consider external Agent-to-Agent protocol adapters later, only when there is a concrete CLI or tool integration that benefits from them.

Queued at `agent-taskboard/2-ready/agent-message-bus-contract/`, `agent-taskboard/2-ready/agent-message-bus-store/`, `agent-taskboard/2-ready/bridge-existing-events-to-message-bus/`, `agent-taskboard/2-ready/project-observability-message-bus-panel/`, `agent-taskboard/2-ready/supporting-agents-message-bus-events/`, and `agent-taskboard/2-ready/system-health-agent-message-bus-review/`.

### Product Runtime Observability

The product should help users understand not only what the agents did, but how the software being built behaves. Software on the workbench should be observable by default: it should emit structured logs, expose useful runtime signals, preserve failure evidence, and make performance and domain behaviour inspectable while the agent is still building it.

This is a separate layer from the Agent Message Bus:

- Agent Message Bus answers: which agent, supervisor, skill, or orchestrator acted, and why.
- Product Runtime Observability answers: what did the built application do when it ran, where did it fail, how fast was it, and which domain events happened.

The first version should be build-time first, production-later. Every serious generated or modified application should have enough observability for local testing, debugging, QA, performance probes, and review. Production deployment hooks can come later, but the build bench should already capture what the app says about itself.

Core layer:

- A project-level observability contract in Markdown that tells agents how this software should log, name domain events, expose metrics, preserve errors, and correlate user actions with runtime effects.
- A small structured runtime event envelope for built software: timestamp, level, event name, subsystem, operation, correlation id, optional job/run/task id, duration, status, error, tags, and payload.
- Default sinks that stay simple: JSONL file, stdout/stderr capture, browser console capture, test-run attachments, and optional HTTP diagnostics endpoint when the app already has a backend.
- Optional adapters for OpenTelemetry or native platform logging later. Do not make OpenTelemetry a hard dependency for the first slice.
- Base prompt guidance that asks coding agents to add or preserve practical observability when they build features: meaningful structured logs, stable event names, error context, performance timings for expensive paths, and enough domain signals to understand what happened.
- An analysis skill that reads runtime logs and answers: what happened, what looks slow, what failed, which errors repeat, which paths are noisy, and which user-visible workflow needs attention.
- A project-level Runtime Observability surface that shows recent product events, error groups, latency summaries, counters, domain-event timelines, and links back to tasks, test runs, screenshots, and agent messages.

This should stay proportional. A tiny script does not need a telemetry platform. A web app, backend service, data pipeline, or product-like tool should have structured logging and a way for the task processor to collect and inspect it while it is under construction.

First implementation order:

1. Define `docs/product-runtime-observability.md` and `docs/schemas/product-runtime-event.schema.json`.
2. Update base prompts and task contracts so agents consider build-time observability when adding meaningful software behaviour.
3. Add capture paths for local runs, Playwright runs, backend logs, and browser console events into task evidence or project observability folders.
4. Add a Runtime Observability project surface that reads structured product events and summarizes errors, latency, counters, and domain timelines.
5. Add an analysis skill or project action that turns logs into reviewable Markdown plus structured JSON findings.
6. Connect runtime events to the Agent Message Bus only by reference, not by mixing the two data models. The bus can say "this run produced runtime log artifact X"; product events remain their own schema.

Queued at `agent-taskboard/2-ready/product-runtime-observability-contract/`, `agent-taskboard/2-ready/base-prompts-observability-guidance/`, `agent-taskboard/2-ready/product-runtime-log-capture/`, `agent-taskboard/2-ready/runtime-observability-project-surface/`, and `agent-taskboard/2-ready/runtime-log-analysis-skill/`.

### Companion App

Make the running task board reachable from a phone without exposing the local processor to the internet:

- A small public relay service holds the latest snapshot and a small command queue. The local processor pushes snapshots and pulls commands on a single outbound HTTPS tick (default 10 s). The phone PWA reads the snapshot and posts commands.
- Pull-pull on the processor side is non-negotiable. The local box never accepts inbound connections; the relay is the only public surface.
- V1 surface: pipeline overview per project, current task, token-spend summary, quota windows, open NEEDS_INPUT decisions, decision-answer command, new-task command, start-job command. No live log stream, no diff viewer, no push notifications.
- V1 secrecy is shared bearer token over TLS. End-to-end encryption with a phone-paired symmetric key is V2; the relay sees plaintext until then.
- The companion shipped on Fly.io. Railway is a documented fallback. The PWA is a separate Angular build, deployed as static assets.
- Default-off in the local processor. Enabling it is a `Companion:Enabled=true` flip in `appsettings.Local.json`, so a fresh checkout never tries to phone home.

The full V1 contract (endpoints, snapshot shape, command shape, sync cadence, file map) lives in [docs/companion-app-design.md](docs/companion-app-design.md). [ADR-0018](docs/architecture-decisions.md) captures the architectural decision.

### Schema-First Communication and In-Memory Data Layer

The product is accumulating cross-cutting structured data: agent messages, participant records, product runtime events, token aggregates per project, supervisor advisories and interventions, audit findings, architecture-quality scores, componentisation metrics. None of this should sit in a database. It should be many small JSON-schema-validated documents on disk, plus a strongly-typed in-memory layer that loads them at boot, supports query and aggregation, and writes back changes the same way the job system already does.

- One schema per concept, named `<concept>.schema.json`, under `docs/schemas/`. Draft 2020-12. English. No em dashes.
- An in-memory store is a typed view over disk; the file is always the source of truth.
- No SQL. No SQLite, no LiteDB, no EF. The repo deliberately avoids a database engine.
- First slice: schemas for supervisor advisory, supervisor intervention, token aggregate, agent message records, and product runtime events, plus an `InMemoryStore` consumed by `AutoInterventionHostedService`, the Agent Message Bus projection, and runtime observability projections to replace direct file reads.

Queued at `agent-taskboard/2-ready/json-schemas-and-in-memory-layer/` and extended by the Agent Message Bus and Product Runtime Observability jobs.

### Continuous Decision Visibility

The orchestrator should not just react at run-end. While a job is in `3-progress`, it should scan recent CLI output every few seconds and surface a "decision required" signal when the agent emits `[[TASK_NEEDS_INPUT:...]]` or another decision sentinel. Today such moments are visible only deep in the activity log; they should be a loud, distinct banner on the project view, with a one-click reply.

- Reuse `AgentOutcomeAnalyzer.SentinelRegex` for detection.
- Piggyback on the existing CLI output poll rather than adding a parallel ticker.
- Banner has its own visual treatment, distinct from supervisor advisories and from regular activity entries.

Queued at `agent-taskboard/2-ready/orchestrator-continuous-decision-visibility/`.

### Dev-Stable Role Split

Dev is a regression-test target, not a self-task target. After a coherent batch ships on stable, dev receives an end-to-end task that exercises the changed surfaces. Dev does not appear in its own watched-projects list. Runtime artefacts (per-project supervisor jsonl, system-review reports, backend logs) belong in `.gitignore`, not in commits.

Queued at `agent-taskboard/2-ready/separate-dev-from-stable-roles/`.

### Backend Observability

The recent silent dev-backend crash exposed an observability gap: the on-disk `.api.log` was four days stale because the running backend redirects to `.api.log.out` / `.api.log.err`, neither of which is rotated, surfaced, or summarised when something dies. After the fix:

- Structured rolling logs at `logs/backend/<date>.log` with traceId, project, jobId fields.
- A `last-crash.json` marker and `/api/diagnostics/last-crash` endpoint so the supervisor and Layer 3 review can pick up crash evidence.
- Layer 3 system-review reads the marker and surfaces it in the next run.

Queued at `agent-taskboard/2-ready/backend-observability-real-logs/`.

### In-Product Concept Documentation

Concepts (Orchestrator, Supervisor, Skills, Audits, Probes, Companion) are documented under `docs/`, but a user looking at a panel cannot reach the docs without leaving the app. A reusable `app-concept-help` component renders an "i" icon on every panel that introduces a concept; the popover shows a short paragraph plus a "Learn more" link to the canonical `docs/` page.

Queued at `agent-taskboard/2-ready/in-product-concept-docs/`.

### Roadmap Intake from the Chat Window

Long branching chat messages with many concerns at once should not require the user to leave the product. The chat window inside the project view gets a "Send to roadmap" mode: a fast-model splitter turns the text into candidate tasks, the user reviews the split, and confirmed candidates land in `1-preparation` (never auto-queued to `2-ready`).

Queued at `agent-taskboard/2-ready/chat-intake-roadmap-from-app/`.

### Focused UX

Keep the app dense, fast, and pleasant to use:

- Compact headers and status bars.
- Better model and CLI defaults.
- Completion notifications that do not interrupt the workflow.
- Layout polish for detail panes, rows, cards, tooltips, and screenshots.

## Hard Boundaries

The core execution model stays intentionally narrow:

- One coding task runs per project at a time.
- Parallelism is allowed across projects, not inside one project.
- The app does not create branches, switch branches, merge branches, or manage worktrees.
- The app does not become a workflow engine.
- The app does not implement its own API-backed coding-agent runtime while subscription CLI agents remain the primary value path.
- Runtime job artifacts belong in watched task folders, not in this source repository.

Planning and research tasks may eventually have a different concurrency model because they do not change source code. That distinction must stay explicit. Coding tasks keep the one-at-a-time rule.

## Agent Decision Principles

When changing this product, prefer work that:

- Reduces human babysitting.
- Makes security review more repeatable, evidence-backed, and frequent.
- Improves review quality.
- Makes the current task state easier to see.
- Preserves the sequential per-project execution model.
- Uses local files and existing subscriptions instead of new hosted infrastructure.
- Treats Codex, Claude Code, and other provider-owned agents as the primary execution engines.
- Keeps the UI compact, legible, and calm.

Be cautious with work that:

- Adds bookkeeping before it removes friction.
- Turns a simple queue into a workflow system.
- Encourages multiple coding agents to edit one project at the same time.
- Rebuilds the provider-owned agent loop before the existing subscription agents have been exhausted.
- Hides important evidence from the reviewer.

## Documentation Drift

After any CLI-executed task finishes, check whether the README, this roadmap, AGENTS.md, [docs/architecture-decisions.md](docs/architecture-decisions.md), or other docs need to be updated. Update them in the same task when the change affects product direction, public behavior, architecture, CLI contracts, filesystem contracts, agent workflow, or established a non-goal worth archiving. The ADR file is the chronological log of decisions; README / ROADMAP / AGENTS are the narrative surfaces that describe the current shape. The two must stay in sync. If no documentation update is needed, say so briefly in the task report.
