# Documentation Index

Hierarchical lookup of every load-bearing document in this repository, with a one-line description of what's inside. Use this as the first stop when an agent or contributor needs to find the right file fast — not a table of contents you read top-to-bottom, but a map you grep against.

The repo's authoritative agent contract lives in [AGENTS.md](../AGENTS.md). This index is a navigation aid; if a rule and AGENTS.md disagree, AGENTS.md wins.

## Top-level entry points

| File | What's inside |
|---|---|
| [README.md](../README.md) | Product pitch, install + run quickstart, primary directory tour. The "what is this and how do I get it running" file. |
| [AGENTS.md](../AGENTS.md) | Single source of truth for agent instructions (Claude Code, Codex CLI, Copilot agent). Edit-only-dev rule, hard product boundaries, regression-proofing doctrine, shell policy, language policy. |
| [ROADMAP.md](../ROADMAP.md) | Product thesis, near-term themes, hard non-goals, decision principles. Read before proposing direction-shifting changes. |
| [CLAUDE.md](../CLAUDE.md) | 3-line compatibility shim that points Claude Code at AGENTS.md. Do not duplicate AGENTS content here. |
| [.github/copilot-instructions.md](../.github/copilot-instructions.md) | 3-line shim for the GitHub Copilot coding agent. Same shape as CLAUDE.md. |

## Architecture, decisions, and contracts

| File | What's inside |
|---|---|
| [architecture-decisions.md](architecture-decisions.md) | The ADR archive (ADR-0001 … ADR-0050). Load-bearing: product boundaries, non-goals, reasoning styles. Bug-fix-grade decisions belong in commits, not here. |
| [adr/adr-0051-task-processing-pipeline.md](adr/adr-0051-task-processing-pipeline.md) | Concept ADR (proposed): CI/CD-style task pipeline. Two step types (llm/script) + common envelope, orchestratorReaction semantics, the derived-SQLite-index DB choice (not EF Core) + DDL, slicing plan. Extends ADR-0045; folds into the archive on acceptance. |
| [architecture-model.md](architecture-model.md) | Marble-style architecture map: <= 10 elements per project, the contract that drift analysis runs against. |
| [design-principles.md](design-principles.md) | UX contract: top-level summary, always-available drill-down, run-as-unit-of-conversation, calm classical style. |
| [design-system.md](design-system.md) | Visual contract: studio-shell tokens, shape/type/motion scale, component inventory, Material 3 Expressive mapping, theme switching. |
| [style-guide/](style-guide/README.md) | Canonical UI vocabulary: tokens, small buttons, pills, cards, modals, tabs, forms, audits, and migration status. Check before adding a new visual variant. |
| [cli-model-selector-audit.md](cli-model-selector-audit.md) | Inventory of every CLI + model selection site in `frontend/src/` and the scoping note for the unified `<app-cli-model-selector>` component. |
| [frontend-scss-quality.md](frontend-scss-quality.md) | SCSS authoring rules + measured audit + 6-wave refactor plan (token consolidation, sidesheet/pane-header extraction, mixins, drop `!important`). |
| [frontend-scss-quality-eval-2026-05-17.md](frontend-scss-quality-eval-2026-05-17.md) | Result of executing the six-wave plan: −396 hex literals, −13 `!important`, three new layout components, three SCSS mixins. Remaining work tier-ordered. |
| [perf-frontend.md](perf-frontend.md) | Frontend perf playbook: visibility-aware polling, cache-first reads, bounded buffers, measurement recipe, anti-patterns. |
| [perf-baseline-2026-05-28.md](perf-baseline-2026-05-28.md) | Baseline for the Accept-to-next-task caching pass: existing instrumentation, missing caches the pass added (SHA-range memo, run-git frontend cache, markdown render LRU), targets, measurement recipe. |
| [frontend-architecture-review-2026-05-09.md](frontend-architecture-review-2026-05-09.md) | Maintainability audit: target component size, mega-component split plan (Tier 1/2/3), service-extraction doctrine. Pairs with ADR-0034. |
| [cli-startup-cost-analysis-2026-05-09.md](cli-startup-cost-analysis-2026-05-09.md) | Per-CLI spawn / probe / discovery costs (Claude / Codex / Copilot / Gemini), `/api/cli/usage` breakdown, ranked optimisation opportunities. Analysis-only. |
| [token-pricing.md](token-pricing.md) | The single per-model price table (`TokenPricing.cs`): rates, cache policy, excluded models, and how per-step + project pipeline cost is derived from it. |
| [filesystem-contract.md](filesystem-contract.md) | Job-folder layout, lane catalog, state strings, the on-disk shape every CLI must respect. |
| [agent-task-contract.md](agent-task-contract.md) | App-owned task lifecycle boundary copied into every watched target. The cross-product contract. |
| [agent-contract-pattern.md](agent-contract-pattern.md) | Contract-bounded agents (ADR-0032): three-zone pattern (Pre-Guard → Agent → Decider+Post-Guard), schemas, decider table, self-heal allow-list, worked example for pickup-failed. The agent classifies, the rule engine decides. |
| [run-outcome-contract.md](run-outcome-contract.md) | Single post-run classification shared by lane routing, `status.md`, and frontend failure-toast surfacing. |
| [loop-inventory.md](loop-inventory.md) | Registry of every place work can re-enter itself (retry, requeue, replay). Each entry carries kind, code anchor, budget constant, breaker test. CI-enforced via `LoopInventoryConsistencyTest`. |
| [architecture-3-progress-lane-writers.md](architecture-3-progress-lane-writers.md) | Inventory of every service that mutates the `3-progress` lane, the boot sequence that orders them, and the `LaneMutexRegistry` (F21) that serialises them per-project. Required reading before adding a seventh writer. |
| [protocol-style.md](protocol-style.md) | `status.md` shape, Activity Log markers, `attachments/` vs `results/`, per-CLI image retention. |
| [commit-push-doctrine.md](commit-push-doctrine.md) | Who owns the git commit + push boundary (the platform, not the CLI). When a CLI is allowed to commit (almost never). |
| [skills-architecture.md](skills-architecture.md) | Portable-skills doctrine: central library plus per-target lookup contract. |
| [supported-clis.md](supported-clis.md) | Cross-CLI invocation contract: what each CLI must satisfy (session model, output format, resume flag, quota probe). |
| [wiki/README.md](wiki/README.md) | Project wiki conventions and the common-problems library. Search this when a familiar runtime, CLI, permission, filesystem, runner, or state-machine failure appears. |

## CLI integration (per-CLI deep refs)

| File | What's inside |
|---|---|
| [cli-skills/README.md](cli-skills/README.md) | Index for the per-CLI skill files. |
| [cli-skills/cli-overview.md](cli-skills/cli-overview.md) | Cross-CLI invariants: stale-session reliability, output-format conventions, the contract every adapter satisfies. |
| [cli-skills/cli-claude.md](cli-skills/cli-claude.md) | Claude Code CLI driver: invocation, stream-json frame catalogue, session-UUID capture, rate-limit pill, quirks, **operator playbook for hangs (ADR-0030)**, fixtures. |
| [cli-skills/cli-codex.md](cli-skills/cli-codex.md) | OpenAI Codex CLI driver: `--json` frame model, session capture, watchdog parity with Claude (ADR-0030), quota probe, common tasks. |
| [codex-runner-investigation.md](codex-runner-investigation.md) | Forensic note on the 2026-05-12 `[[TASK_NOOP]]`-per-job regression: Codex 0.130 positional-PROMPT semantics, the failed close-stdin attempt, the stdin-via-`-` fix, and the regression coverage that locks it. |
| [cli-skills/cli-copilot.md](cli-skills/cli-copilot.md) | GitHub Copilot CLI driver: PTY interaction, slash-command probes, model handling. |
| [cli-skills/cli-gemini.md](cli-skills/cli-gemini.md) | Google Gemini CLI driver: stream-json parsing, session capture, /stats PTY probe. |
| [cli-skills/sandbox-and-yolo.md](cli-skills/sandbox-and-yolo.md) | Per-CLI permission/sandbox/YOLO modes: why YOLO is the default, the mode → flags table for all four CLIs, the per-project override surface, and the `effective-mode` probe + `source` semantics. |

## Setup and onboarding (operator-facing)

| File | What's inside |
|---|---|
| [setup/README.md](setup/README.md) | Operator-facing setup guide index: attach a project, onboard a CLI, first task walkthrough, troubleshooting. Companion to `getting-started.md`. |
| [setup/onboard-a-project.md](setup/onboard-a-project.md) | `WatchPaths` entry, backend restart, per-project defaults (`RunnerMode`, `AutoCommit`, `OrchestratorModel`, `AutonomyLevel`), first-task expectations. |
| [setup/onboard-an-agent-cli.md](setup/onboard-an-agent-cli.md) | Per-CLI install / config / quirks (Claude, Codex, Copilot, Gemini). Includes the load-bearing Codex Windows-sandbox quirk and the cross-CLI sentinel-awareness note. |
| [setup/your-first-task.md](setup/your-first-task.md) | "Project Overview Doc" pattern as a good first task, anti-patterns, where to watch the run, pointer to the Job API skill for scripted creation. |
| [setup/troubleshooting.md](setup/troubleshooting.md) | FAQ-style: sandbox-only errors, auto-mode flip to manual, two jobs in 3-progress, missing-terminal-sentinel, crash-recovery auto-commits, `watchPath` quirk on the API. |

## Process surfaces (what each app surface owns)

| File | What's inside |
|---|---|
| [agent-message-bus.md](agent-message-bus.md) | The message-bus channel between supporting agents (orchestrator, supervisor, runners). |
| [analysis-reports.md](analysis-reports.md) | Markdown-plus-structured-block report contract for any spawnable analysis (security review, drift, council critique). |
| [drift-reports.md](drift-reports.md) | Drift dimensions, scoring, the report shape used by the project Drift surface. |
| [companion-app-design.md](companion-app-design.md) | The outbound-only companion-app relay (ADR-0018). |
| [concept-docs/](concept-docs/) | Short in-product explainers, one per topic. Served by `GET /api/concept-docs/{topic}` and rendered in the `<app-info-button>` side-drawer next to surfaces whose behaviour is non-obvious (e.g. lane headers). |

## Mockups (locked design references)

Each mockup is a click-dummy plus a design narrative. Implementation slices reference these by path; do not reinvent them per task.

| Folder | What's inside |
|---|---|
| [mockups/quality-system/](mockups/quality-system/) | Project-page shell with left-rail navigation (Overview/Security/Architecture/UX-UI/Test-Quality/Token-Usage/Audits/Jobs/Settings/Orchestrator/Activity). 15-step "First Implementation Slice" plan. |
| [mockups/kanban-board-design/](mockups/kanban-board-design/) | Kanban grid taxonomy: lane widths, header treatments, hide-when-empty rules, amber accent for `3a-failed-pickup`. |
| [mockups/orchestrator-meta-cycle/](mockups/orchestrator-meta-cycle/) | Layer-2.5 meta-cycle UX (ADR-0022): pause-inspect-resume at quiet batch boundaries. |
| [mockups/task-progress-tracking/](mockups/task-progress-tracking/) | Per-job plan strip above the activity log. Parses Claude `TodoWrite` / Codex `update_plan` frames; live tool-call ticker, soft-estimate band, heartbeat pulse, expandable sub-actions per completed item. No LLM calls. |
| [mockups/orchestrator-prep-and-autonomy/](mockups/orchestrator-prep-and-autonomy/) | The `1a-orchestrator-prep` lane + autonomy scale (ADR-0026). |
| [mockups/chat-window-next-gen/](mockups/chat-window-next-gen/) | Project chat redesign: markdown rendering, embedded events, side rail, endless history. Source for the `project-chat-becomes-primary-surface-with-embedded-events` job and its slices. |
| [mockups/vscode-layout/](mockups/vscode-layout/) | VS Code-shape chrome experiment behind the `Frontend:VsCodeLayout` flag. |
| [mockups/task-processing-pipeline/](mockups/task-processing-pipeline/) | CI/CD-style pipeline (ADR-0051): the project pipeline editor (drag-reorder, per-step config, AI-assist) and the task-detail timeline (planned steps + live progress + per-step artifact + orchestrator verdict). Static ASCII at concept stage. |

## Research (deep dives that inform decisions)

Read these before re-litigating a decision they already explored.

| File | What's inside |
|---|---|
| [research/cli-orchestration-survey-2026-05.md](research/cli-orchestration-survey-2026-05.md) | Cross-orchestrator survey (hcom, gate4agent, opencode, …): claude-code#771, default-deny stdin, PTY-vs-pipe trade-offs. The grounding for ADR-0014. |
| [research/wsl2-vs-windows-decision-2026-05.md](research/wsl2-vs-windows-decision-2026-05.md) | Why we did not require WSL2: per-failure-mode evidence. The grounding for ADR-0015. |
| [research/orchestrator-meta-loop-analysis-2026-05-04.md](research/orchestrator-meta-loop-analysis-2026-05-04.md) | Multi-loop supervision (Layers 0-3): runner, supervisor, meta-cycle, system-review. The grounding for ADR-0017 and ADR-0022. |
| [research/orchestrator-decision-protocol-2026-05.md](research/orchestrator-decision-protocol-2026-05.md) | Deterministic post-run policy that became `RunOutcomePolicy`. The grounding for ADR-0002. |
| [research/expanded-lifecycle-lanes-plan-2026-05.md](research/expanded-lifecycle-lanes-plan-2026-05.md) | Lane catalog evolution (ADR-0025/0026/0028/0029). |
| [research/auto-pickup-cascade-analysis-2026-05.md](research/auto-pickup-cascade-analysis-2026-05.md) | The "5 jobs entered 3-progress in 10 s" post-mortem; informs ADR-0028's strict-iteration pickup. |
| [research/arhciv-loop-postmortem-2026-05.md](research/arhciv-loop-postmortem-2026-05.md) | The 31-run loop where a stuck `3-progress` folder vanished into `7-archive`; informs ADR-0029. |
| [research/embedded-chat-integration-2026-05.md](research/embedded-chat-integration-2026-05.md) | The grounding for the project-chat-redesign work. |
| [research/project-chat-progress-indicator-2026-05-08.md](research/project-chat-progress-indicator-2026-05-08.md) | Progress indicator + responsiveness analysis: current state, competitor patterns, latency budgets, redesign recommendation (caret-pulse + wall-clock state ladder, no backend change in v1). |
| [research/kanban-layout-reconciliation-2026-05.md](research/kanban-layout-reconciliation-2026-05.md) | Kanban layout drift between docs/mockups and the live frontend. |
| [research/path-forward-plan-2026-05.md](research/path-forward-plan-2026-05.md) | Phase plan that the autonomy-scale work executes against. |
| [research/planning-research-task-kinds-2026-05.md](research/planning-research-task-kinds-2026-05.md) | Task-kind taxonomy (Bug / User Story / Chore) that informed the backlog-lane work. |
| [research/runner-outcome-visibility-2026-05-11.md](research/runner-outcome-visibility-2026-05-11.md) | Concrete runner outcome categories replacing broad heuristicfallback for permission blocks, watchdog kills, missing sentinels, and classifier misses. |
| [research/auto-review-postprocessing-consolidation-2026-06.md](research/auto-review-postprocessing-consolidation-2026-06.md) | Why `4-auto-review` stalls (decoupled off-by-default poll) and how to consolidate review into synchronous orchestrator post-processing as its own task/step, decoupled from runner throughput. Grounding for the `post-processing-orchestrator-lane` epic (ASS-176). |
| [research/orchestrator-prep-as-active-pipeline-step-2026-06.md](research/orchestrator-prep-as-active-pipeline-step-2026-06.md) | The pre-side sibling: retire the `1a-orchestrator-prep` lane and converge it with the intake phase into one optional, parallelizable Pre pipeline step that gates `2-ready -> 3-progress`, uses the project model, and shows status/duration in the pipeline table. Supersedes ADR-0026's lane; fills the ADR-0045 Pre slot. |

## Schemas (the wire / disk shapes)

JSON Schemas pinned by tests. If you change one, update the corresponding fixture.

| File | What it shapes |
|---|---|
| [schemas/agent-message.schema.json](schemas/agent-message.schema.json) | Messages on the agent message bus. |
| [schemas/agent-participant.schema.json](schemas/agent-participant.schema.json) | Participant records (orchestrator, supervisor, agent, user). |
| [schemas/agent-artifact-ref.schema.json](schemas/agent-artifact-ref.schema.json) | References to artifacts produced during a run. |
| [schemas/architecture-model.schema.json](schemas/architecture-model.schema.json) | Marble architecture-model entries (drift-analysis input). |
| [schemas/analysis-report.schema.json](schemas/analysis-report.schema.json) | The markdown-plus-structured-block analysis report contract. |
| [schemas/drift-report.schema.json](schemas/drift-report.schema.json) | Drift-dimension scoring rows. |
| [schemas/orchestrator-decision.schema.json](schemas/orchestrator-decision.schema.json) | Typed orchestrator chat-log entries (decision / reissue / heuristic / giveup). |
| [schemas/meta-cycle-report.schema.json](schemas/meta-cycle-report.schema.json) | Meta-cycle pause-inspect-resume report shape (ADR-0022). |
| [schemas/executive-summary.schema.json](schemas/executive-summary.schema.json) | Cross-project executive summary contract. |
| [schemas/client-identity.schema.json](schemas/client-identity.schema.json) | Registered client identity records. |
| [schemas/orphan-recovery.schema.json](schemas/orphan-recovery.schema.json) | Stale-progress-archiver decision rows. Per ADR-0051 stale folders requeue to `2-ready` or archive to `7-archive`; the legacy `failedPickupSlug` / `failureKind` fields are kept only for old on-disk rows. |
| [schemas/pipeline-definition.schema.json](schemas/pipeline-definition.schema.json) | One versioned project pipeline definition: ordered pre/post steps, common envelope, llm/script types (ADR-0051). |
| [schemas/step-run.schema.json](schemas/step-run.schema.json) | One (task, step, attempt) telemetry row in `logs/step-runs.jsonl`; the source of truth the derived `pipeline-history.db` projects (ADR-0051). |
| [schemas/pickup-failure.schema.json](schemas/pickup-failure.schema.json) | Live-pickup dead-letter rows (ADR-0028). |
| [schemas/product-runtime-event.schema.json](schemas/product-runtime-event.schema.json) | Runtime events captured during a CLI run. |
| [schemas/protocol-header.schema.json](schemas/protocol-header.schema.json) | `status.md` protocol-header structured block. |
| [schemas/supervisor-advisory.schema.json](schemas/supervisor-advisory.schema.json) | Supervisor advisory records (ADR-0017). |
| [schemas/supervisor-intervention.schema.json](schemas/supervisor-intervention.schema.json) | Supervisor pre-emptive primitives (`cancelRun`, `pausePickup`, `forceFail`, `resume`). |
| [schemas/task-find-result.schema.json](schemas/task-find-result.schema.json) | Task search-result rows. |
| [schemas/task-mutation-request.schema.json](schemas/task-mutation-request.schema.json) | Mutation requests through the Task Access layer (ADR-0024). |
| [schemas/token-aggregate.schema.json](schemas/token-aggregate.schema.json) | Aggregate token usage rows (project / job rollups). |
| [schemas/token-aggregate-by-client.schema.json](schemas/token-aggregate-by-client.schema.json) | Token aggregates split by registered client identity. |
| [schemas/token-timeline-bucket.schema.json](schemas/token-timeline-bucket.schema.json) | Time-bucketed token usage (drives the timeline panel). |

## Working with this index

- **Looking for a CLI quirk?** Start at the matching `cli-skills/cli-<name>.md`.
- **Looking for the rationale of a load-bearing decision?** Search `architecture-decisions.md` first; ADRs cite their grounding research files.
- **Looking for a mockup before implementing a slice?** Start at the relevant `mockups/<surface>/README.md`.
- **Looking for a wire / disk shape?** `schemas/`.
- **Looking for "should I do this at all?"** ROADMAP.md (product thesis, hard non-goals).
- **Looking for the agent-side rules?** AGENTS.md is authoritative; this index points to it.

When a new top-level document lands, add a one-line row above so this stays a single grep target.
