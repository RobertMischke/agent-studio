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
- A design mockup for a broader quality system under `docs/mockups/quality-system/`. This is exploratory, not product behavior yet.
- A design mockup for creative design loops and council-style critique under `docs/mockups/creative-design-system/`. This is exploratory, not product behavior yet.

## Roadmap Themes

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
- Put UX/UI and Test Quality directly on the project page as first-class menu entries. UX/UI collects design references and iteration evidence; Test Quality collects test runs, coverage, tuning results, source maps, and code metrics.
- Let the orchestrator steer a design loop explicitly: accept a version, request another version, ask for a harsher critique, or turn council feedback into normal queued implementation tasks.
- Preserve design history in task evidence: screenshot sets, variant notes, council verdicts, chosen direction, rejected alternatives, and follow-up tasks.

First implementation order:

1. UX/UI and Test Quality project menu surfaces beside Security and Architecture.
2. Design Loop concept and task-evidence shape for screenshot variants, references, QA runs, source maps, and council notes.
3. Design reference library for screenshots, markdown briefs, images, accepted examples, and rejected alternatives.
4. Screenshot comparison panel on the task detail surface.
5. Local design Skill definitions for screenshot critique, UI polish, copy tone, and accessibility design review.
6. Testing and QA run history for backend tests, end-to-end tests, tuning tests, coverage, and code metrics.
7. Source-code map action that visualizes modules, lines of code, ownership areas, and organization concerns.
8. Council review prompt that produces role-separated critique and a final orchestrator recommendation.
9. "Next version" action that creates a follow-up design iteration task from the current screenshots and council notes.
10. Project-level design memory: chosen visual direction, brand notes, UI principles, and examples of accepted screens.

### Project Control

Make each watched project easier to inspect and operate:

- Project detail pages with path, configuration, status, and quick actions.
- Project dimensions for Security and Architecture, with current status plus historical Markdown records.
- Clearer manual start vs. auto-pickup behavior.
- Safer locking once a task has started, so completed or running work does not drift to another project by accident.
- Better visibility into active CLI sessions that may already be working in the same project.
- Repository hygiene for accepted tasks: surface dirty and unpushed work, support prompt commits of accepted task changes and task evidence, and prevent completed work from quietly piling up on disk.

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
- A **continuous protocol** per project: append-only structured logs the user can replay to see what the supervisor saw and decided. A panel on the project page shows the live state.
- A **system review monitor**, run from outside on a multi-hour cadence, that produces structured "after ten hours, this is what the system did and this is what looks off" reports. Stand-alone, not part of the app's runtime, so it survives any app failure mode.

Hard boundary: supervision is advice-first, force-rare. The deterministic post-run policy in `RunOutcomePolicy.cs` stays the authoritative path for routine outcomes; the supervisor adds an outer kill-switch and a soft-reasoning second opinion, not a parallel orchestrator.

The full conceptual analysis - loop-to-loop control options, communication contract sketch, execution-model tradeoffs (in-process vs sidecar vs CLI-driven), open conceptual problems, and recommended task spinout - lives in [docs/research/orchestrator-meta-loop-analysis-2026-05-04.md](docs/research/orchestrator-meta-loop-analysis-2026-05-04.md). The recommended first slice is the system review monitor (Layer 3) because it ships value immediately on stable without touching the runtime.

### Companion App

Make the running task board reachable from a phone without exposing the local processor to the internet:

- A small public relay service holds the latest snapshot and a small command queue. The local processor pushes snapshots and pulls commands on a single outbound HTTPS tick (default 10 s). The phone PWA reads the snapshot and posts commands.
- Pull-pull on the processor side is non-negotiable. The local box never accepts inbound connections; the relay is the only public surface.
- V1 surface: pipeline overview per project, current task, token-spend summary, quota windows, open NEEDS_INPUT decisions, decision-answer command, new-task command, start-job command. No live log stream, no diff viewer, no push notifications.
- V1 secrecy is shared bearer token over TLS. End-to-end encryption with a phone-paired symmetric key is V2; the relay sees plaintext until then.
- The companion shipped on Fly.io. Railway is a documented fallback. The PWA is a separate Angular build, deployed as static assets.
- Default-off in the local processor. Enabling it is a `Companion:Enabled=true` flip in `appsettings.Local.json`, so a fresh checkout never tries to phone home.

The full V1 contract (endpoints, snapshot shape, command shape, sync cadence, file map) lives in [docs/companion-app-design.md](docs/companion-app-design.md). [ADR-0018](docs/architecture-decisions.md) captures the architectural decision.

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
