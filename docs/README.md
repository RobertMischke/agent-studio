# Documentation Index

This folder is the physical knowledge root. The Wiki renders this real folder
tree directly: categories are directories, pages are Markdown or sandboxed HTML
files, and there is no virtual grouping layer.

Use this page as the first stop when you need the right document quickly.

## Categories

| Folder | What lives there |
|---|---|
| [architecture/](architecture/README.md) | Architecture model, ADR archive, proposed ADRs, backend structure, bus docs, runner-lane constraints, and HTML maps. |
| [domains/](domains/README.md) | Current system-of-record domain maps for runner, pipeline, tasks, frontend, CLI, tokens, and token pricing. |
| [contracts/](contracts/README.md) | Durable filesystem, task, protocol, run outcome, code-pattern, and wiki organization contracts. |
| [design/](design/README.md) | Product-wide, prompt-known design hard rules (no left accent bars, full-bleed views, aggregate = sum, acute-only signals, both themes). |
| [quality/](quality/README.md) | Technology-aware Angular and .NET style guides, applicability metadata, and the rule-authoring workflow used by Project Hub and intake prompts. |
| [product/](product/README.md) | Product and UX direction: design principles, orchestrator chat, companion app, and skills architecture. |
| [frontend/](frontend/README.md) | Frontend design system, style guide, testing contract, performance playbook, and audits. |
| [cli/](cli/README.md) | Supported CLI contract, per-CLI skills, audits, and investigations. |
| [operations/](operations/README.md) | Setup, onboarding, security docs, runtime observability, git doctrine, and test workspaces. |
| [reports/](reports/README.md) | Report contracts, HTML visual reports, and screenshot-backed visual documentation. |
| [meta/](meta/README.md) | Document metadata, drift grading samples, direction, and HTML metadata reports. |
| [concepts/](concepts/README.md) | Architecture concepts that have not become a domain or ADR yet, plus hand-maintained living concept/knowledge pages with running knowledge logs. |
| [in-app-help/](in-app-help/README.md) | Short Markdown help pages served by the app next to non-obvious UI surfaces. |
| [common-problems/](common-problems/README.md) | Known recurring problems with root-cause analysis, occurrence logs, and workarounds. Search here first on a familiar symptom. |
| [learnings/](learnings/README.md) | Per-task run learnings auto-distilled by the opt-in `post-wiki-learnings` pipeline step. Do not edit by hand. |
| [research/](research/) | Dated deep dives and decision grounding material. |
| [schemas/](schemas/README.md) | JSON schemas for wire and disk shapes. |
| [mockups/](mockups/) | Locked design references and click-dummies. |
| [assets/](assets/) | Image assets referenced by documentation pages. |
| [proposals/](proposals/README.md) | Dated improvement proposals with durable approval status and implementation-card references. |

## Load-Bearing Entry Points

| Topic | Start here |
|---|---|
| Runner | [domains/runner.md](domains/runner.md) |
| Pipeline | [domains/pipeline.md](domains/pipeline.md) |
| Tasks | [domains/tasks.md](domains/tasks.md) |
| Frontend | [domains/frontend.md](domains/frontend.md) |
| Global search palette and API | [domains/frontend.md#global-search](domains/frontend.md#global-search) |
| Frontend navigation style guide | [frontend/style-guide/navigation.md](frontend/style-guide/navigation.md) |
| CLI | [domains/cli.md](domains/cli.md) |
| Tokens | [domains/tokens.md](domains/tokens.md) |
| ADR archive | [architecture/decisions/adr-archive.md](architecture/decisions/adr-archive.md) |
| Architecture model | [architecture/model.md](architecture/model.md) |
| Architecture and Quality layer (Project Map, mapped guides, analysis inventory, component grading) | [concept](concepts/architecture-quality-layer.md) · [interactive Workbench](workbenches/architecture-quality-layer/index.html) |
| Managed project manifest map | [architecture/project-map.md](architecture/project-map.md) |
| Agent message bus | [architecture/bus/agent-message-bus.md](architecture/bus/agent-message-bus.md) |
| Design hard rules (prompt-known) | [design/style-guide-hard-rules.md](design/style-guide-hard-rules.md) |
| Engineering style guides (technology-aware and prompt-injected) | [quality/README.md](quality/README.md) |
| Application visual survey (2026-07-11) | [design/app-survey-2026-07-11.html](design/app-survey-2026-07-11.html) |
| Angular performance review (2026-07) | [design/angular-performance-report-2026-07.html](design/angular-performance-report-2026-07.html) |
| Explorer project state indicator exploration (2026-07) | [design/tree-indicator-exploration-2026-07.html](design/tree-indicator-exploration-2026-07.html) and [alternatives catalog](concepts/tree-project-indicator-alternatives.md) |
| UX doctrine | [product/design-principles.md](product/design-principles.md) |
| Persistent orchestrator chat and session-turn API | [product/orchestrator-chat.md](product/orchestrator-chat.md) |
| In-app orchestrator sight, tools, and anchor slices | [concepts/orchestrator-in-app.md](concepts/orchestrator-in-app.md) |
| Wiki Pulse dashboard (change feed + inbox + drift grading; PULSE-1) | [concepts/wiki-pulse-dashboard.md](concepts/wiki-pulse-dashboard.md) |
| Wiki grading run (global LLM grade per page; trigger + critical pages; GRADE-1) | [concepts/wiki-grading-run.md](concepts/wiki-grading-run.md) |
| Run-liveness & slot semantics (heartbeat, process-lost demotion) | [concepts/run-liveness-and-slot-semantics.md](concepts/run-liveness-and-slot-semantics.md) |
| Result view and case templates | [concepts/result-view-and-case-templates.md](concepts/result-view-and-case-templates.md) |
| Publishing workflows (publish-target derivation + pending badges; PUB-1) | [concepts/publishing-workflows.md](concepts/publishing-workflows.md) |
| Git-info performance measurements and cache invalidation (AGT-2007) | [reports/git-info-performance-agt-2007.md](reports/git-info-performance-agt-2007.md) |
| Planning-task lifecycle (plan → spawn → accept; spawn-contract gate; AGT-2069) | [concepts/planning-task-lifecycle.md](concepts/planning-task-lifecycle.md) |
| Experiment workbenches (Explorer, orchestrator chat, decision-to-task) | [concept](concepts/experimentier-workbench.md) · [interactive mockup](concepts/mockups/experimentier-workbench.html) |
| Decoupled agent-session lifecycles (holder, consumer channel, multi-client attach) | [concept](concepts/decoupled-lifecycles.md) · [interactive Workbench-family mockup](concepts/mockups/decoupled-lifecycles.html) |
| Distributed Agent Studio target architecture (Studio, Task Server, Runner, security, lifecycle, and component projects) | [concepts/distributed-agent-studio-target-architecture.md](concepts/distributed-agent-studio-target-architecture.md) |
| Deployment as a first-class citizen (Deployment page, scenario templates, prompt-defined dynamic UI over CLI tasks; DEP-1..5) | [concept](concepts/deployment-first-class.md) · [interactive mockup](concepts/mockups/deployment-first-class.html) |
| Release semantics (integration vs acceptance vs release vs stable freeze; transparent watering-can model) | [concept](concepts/release-semantics.md) |
| Project Overview operator dashboard (metrics, Visual Evidence Queue, Project URLs, deployment summary, Wiki entry) | [mockup contract](mockups/project-overview-dashboard/README.md) · [interactive mockup](mockups/project-overview-dashboard/ui.html) |
| Wiki classification | [product/wiki-document-classification.md](product/wiki-document-classification.md) |
| Wiki editing flow | [product/wiki-editing-and-branch-flow.md](product/wiki-editing-and-branch-flow.md) |
| Wiki document companion schema | [schemas/wiki-document-companion.schema.json](schemas/wiki-document-companion.schema.json) |
| Model qualification benchmark event schema | [schemas/model-qualification-event.schema.json](schemas/model-qualification-event.schema.json) |
| Wiki drift audit | [meta/reports/wiki-drift-audit-2026-06-11.html](meta/reports/wiki-drift-audit-2026-06-11.html) |
| Supported CLIs | [cli/supported-clis.md](cli/supported-clis.md) |
| Getting started (new install, step by step) | [operations/setup/getting-started.md](operations/setup/getting-started.md) |
| Setup | [operations/setup/README.md](operations/setup/README.md) |
| Standalone remote runner (Linux host) | [operations/setup/linux-runner-host.md](operations/setup/linux-runner-host.md) |
| Remote hosts operator lifecycle | [operations/remote-hosts.md](operations/remote-hosts.md) |
| Remote runner persistent connection (tunnel-as-a-service + health-check) | [operations/setup/remote-runner-persistent-connection.md](operations/setup/remote-runner-persistent-connection.md) |
| Common problems | [common-problems/README.md](common-problems/README.md) |
| Designated topics (AGENTS/wiki-sync current-state index; post-agents-wiki-sync) | [concepts/designated-topics/README.md](concepts/designated-topics/README.md) |
| API project identity / watchPath | [concepts/api-project-identity-and-watchpath.md](concepts/api-project-identity-and-watchpath.md) |
| Workflow arguments become unbounded fan-out | [common-problems/workflow-args-json-string-fanout/](common-problems/workflow-args-json-string-fanout/) |
| Services killed by a harness sweep | [common-problems/services-killed-by-harness-sweep/](common-problems/services-killed-by-harness-sweep/) |
| Orchestrator drive-to-conclusion & CLI-crash resilience | [concepts/orchestrator-drive-to-conclusion.html](concepts/orchestrator-drive-to-conclusion.html) |
| Task integration & worktree/merge workflow | [concepts/task-integration-and-merge-workflow.md](concepts/task-integration-and-merge-workflow.md) |
| Merge config analysis (parallelism coupling) | [concepts/task-integration-merge-config-analysis.html](concepts/task-integration-merge-config-analysis.html) |
| Auto-review reissue / evidence-gate analysis | [concepts/auto-review-evidence-gate-analysis.html](concepts/auto-review-evidence-gate-analysis.html) |
| Completion, review, runner provenance, host handoff, and Remote Runner stability | [concepts/completion-review-and-remote-runner-stability.html](concepts/completion-review-and-remote-runner-stability.html) |
| Planning & Research task type | [concepts/planning-research-task-type.html](concepts/planning-research-task-type.html) |
| MVP presentation screen-tooling evaluation and recommendation | [research/screen-tooling-mvp-presentation-2026-07.md](research/screen-tooling-mvp-presentation-2026-07.md) |
| MVP presentation capture runbook | [operations/setup/presentation-capture.md](operations/setup/presentation-capture.md) |
| MVP presentation storyboard and shot list | [product/mvp-presentation-storyboard.md](product/mvp-presentation-storyboard.md) |
| Quota snapshot events at run start/end (cap-forecast data collection) | [concepts/quota-snapshot-run-events.md](concepts/quota-snapshot-run-events.md) |
| Runtime prompt usage audit | [concepts/runtime-prompt-usage-audit.html](concepts/runtime-prompt-usage-audit.html) |
| Admin CLI onboarding | [concepts/admin-cli-onboarding.html](concepts/admin-cli-onboarding.html) |
| Orchestrator supervision loop | [concepts/orchestrator-supervision-loop.html](concepts/orchestrator-supervision-loop.html) |
| Runner stability & incident chronicle (incidents, invariants, sessions; supersedes the retired runner-stability / overnight / claude-termination pages) | [workbenches/haertung-verteilte-ausfuehrung/historie.html](workbenches/haertung-verteilte-ausfuehrung/historie.html) |
| Process termination & abort scenarios (test suite) | [concepts/process-termination-scenarios.html](concepts/process-termination-scenarios.html) |
| Wiki consolidation analysis 2026-07-18 (inventory, redundancy clusters, cleanup plan) | [konsolidierung-analyse-2026-07-18.md](konsolidierung-analyse-2026-07-18.md) |

## Organization Rules

- Keep source-of-truth contracts in `domains/`, `contracts/`, or
  `architecture/`.
- Keep dated evidence in `reports/`, `research/`, `frontend/audits/`, or
  `cli/audits/`.
- Keep explanatory and evolving notes in `concepts/`, recurring incident
  patterns in `common-problems/`, and auto-distilled run learnings in
  `learnings/` (wiki conventions: [README-aus-wiki.md](README-aus-wiki.md)).
- Use Markdown by default.
- Use HTML only for visual or spatial pages that need layout, such as maps or
  reports. HTML pages must be self-contained and readable in the Wiki iframe.
- When adding a new document, put it in its real category and add it to the
  nearest category index.
