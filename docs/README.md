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
| [product/](product/README.md) | Product and UX direction: design principles, orchestrator chat, companion app, and skills architecture. |
| [frontend/](frontend/README.md) | Frontend design system, style guide, testing contract, performance playbook, and audits. |
| [cli/](cli/README.md) | Supported CLI contract, per-CLI skills, audits, and investigations. |
| [operations/](operations/README.md) | Setup, onboarding, security docs, runtime observability, git doctrine, and test workspaces. |
| [reports/](reports/README.md) | Report contracts, HTML visual reports, and screenshot-backed visual documentation. |
| [meta/](meta/README.md) | Document metadata, drift grading samples, direction, and HTML metadata reports. |
| [concepts/](concepts/) | Future/current architecture concepts that have not become a domain or ADR yet. |
| [in-app-help/](in-app-help/README.md) | Short Markdown help pages served by the app next to non-obvious UI surfaces. |
| [wiki/](wiki/README.md) | Living knowledge: common problems, explanatory concept pages, learnings, and migration notes. |
| [research/](research/) | Dated deep dives and decision grounding material. |
| [schemas/](schemas/README.md) | JSON schemas for wire and disk shapes. |
| [mockups/](mockups/) | Locked design references and click-dummies. |
| [assets/](assets/README.md) | Image assets referenced by documentation pages. |
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
| Agent message bus | [architecture/bus/agent-message-bus.md](architecture/bus/agent-message-bus.md) |
| Design hard rules (prompt-known) | [design/style-guide-hard-rules.md](design/style-guide-hard-rules.md) |
| Application visual survey (2026-07-11) | [design/app-survey-2026-07-11.html](design/app-survey-2026-07-11.html) |
| Angular performance review (2026-07) | [design/angular-performance-report-2026-07.html](design/angular-performance-report-2026-07.html) |
| Explorer project state indicator exploration (2026-07) | [design/tree-indicator-exploration-2026-07.html](design/tree-indicator-exploration-2026-07.html) and [alternatives catalog](wiki/concepts/tree-project-indicator-alternatives.md) |
| UX doctrine | [product/design-principles.md](product/design-principles.md) |
| Persistent orchestrator chat and session-turn API | [product/orchestrator-chat.md](product/orchestrator-chat.md) |
| In-app orchestrator sight, tools, and anchor slices | [concepts/orchestrator-in-app.md](concepts/orchestrator-in-app.md) |
| Workstream frame (top wiki element; internal name engineering-workstream) | [concepts/engineering-workstream.md](concepts/engineering-workstream.md) |
| Project relationship model and branch-aware Wiki checkout | [concepts/project-relationship-model.md](concepts/project-relationship-model.md) |
| Wiki Pulse dashboard (change feed + inbox + drift grading; PULSE-1) | [concepts/wiki-pulse-dashboard.md](concepts/wiki-pulse-dashboard.md) |
| Wiki grading run (global LLM grade per page; trigger + critical pages; GRADE-1) | [concepts/wiki-grading-run.md](concepts/wiki-grading-run.md) |
| Run-liveness & slot semantics (heartbeat, process-lost demotion) | [concepts/run-liveness-and-slot-semantics.md](concepts/run-liveness-and-slot-semantics.md) |
| Result view and case templates | [concepts/result-view-and-case-templates.md](concepts/result-view-and-case-templates.md) |
| Publishing workflows (publish-target derivation + pending badges; PUB-1) | [concepts/publishing-workflows.md](concepts/publishing-workflows.md) |
| Git-info performance measurements and cache invalidation (AGT-2007) | [reports/git-info-performance-agt-2007.md](reports/git-info-performance-agt-2007.md) |
| Planning-task lifecycle (plan → spawn → accept; spawn-contract gate; AGT-2069) | [concepts/planning-task-lifecycle.md](concepts/planning-task-lifecycle.md) |
| Experiment workbenches (Explorer, orchestrator chat, decision-to-task) | [concept](concepts/experimentier-workbench.md) · [interactive mockup](concepts/mockups/experimentier-workbench.html) |
| Wiki classification | [product/wiki-document-classification.md](product/wiki-document-classification.md) |
| Wiki editing flow | [product/wiki-editing-and-branch-flow.md](product/wiki-editing-and-branch-flow.md) |
| Wiki document companion schema | [schemas/wiki-document-companion.schema.json](schemas/wiki-document-companion.schema.json) |
| Wiki drift audit | [meta/reports/wiki-drift-audit-2026-06-11.html](meta/reports/wiki-drift-audit-2026-06-11.html) |
| Supported CLIs | [cli/supported-clis.md](cli/supported-clis.md) |
| Getting started (new install, step by step) | [operations/setup/getting-started.md](operations/setup/getting-started.md) |
| Setup | [operations/setup/README.md](operations/setup/README.md) |
| Standalone remote runner (Linux host) | [operations/setup/linux-runner-host.md](operations/setup/linux-runner-host.md) |
| Remote runner persistent connection (tunnel-as-a-service + health-check) | [operations/setup/remote-runner-persistent-connection.md](operations/setup/remote-runner-persistent-connection.md) |
| Common problems | [wiki/common-problems/README.md](wiki/common-problems/README.md) |
| Designated topics (AGENTS/wiki-sync current-state index; post-agents-wiki-sync) | [wiki/concepts/designated-topics/README.md](wiki/concepts/designated-topics/README.md) |
| API project identity / watchPath | [wiki/concepts/api-project-identity-and-watchpath.md](wiki/concepts/api-project-identity-and-watchpath.md) |
| Workflow arguments become unbounded fan-out | [wiki/common-problems/workflow-args-json-string-fanout/](wiki/common-problems/workflow-args-json-string-fanout/) |
| Services killed by a harness sweep | [wiki/common-problems/services-killed-by-harness-sweep/](wiki/common-problems/services-killed-by-harness-sweep/) |
| Orchestrator drive-to-conclusion & CLI-crash resilience | [wiki/concepts/orchestrator-drive-to-conclusion.html](wiki/concepts/orchestrator-drive-to-conclusion.html) |
| Task integration & worktree/merge workflow | [wiki/concepts/task-integration-and-merge-workflow.md](wiki/concepts/task-integration-and-merge-workflow.md) |
| Merge config analysis (parallelism coupling) | [wiki/concepts/task-integration-merge-config-analysis.html](wiki/concepts/task-integration-merge-config-analysis.html) |
| Auto-review reissue / evidence-gate analysis | [wiki/concepts/auto-review-evidence-gate-analysis.html](wiki/concepts/auto-review-evidence-gate-analysis.html) |
| Planning & Research task type | [wiki/concepts/planning-research-task-type.html](wiki/concepts/planning-research-task-type.html) |
| MVP presentation screen-tooling evaluation and recommendation | [research/screen-tooling-mvp-presentation-2026-07.md](research/screen-tooling-mvp-presentation-2026-07.md) |
| MVP presentation capture runbook | [operations/setup/presentation-capture.md](operations/setup/presentation-capture.md) |
| MVP presentation storyboard and shot list | [product/mvp-presentation-storyboard.md](product/mvp-presentation-storyboard.md) |
| Runtime prompt usage audit | [wiki/concepts/runtime-prompt-usage-audit.html](wiki/concepts/runtime-prompt-usage-audit.html) |
| Admin CLI onboarding | [wiki/concepts/admin-cli-onboarding.html](wiki/concepts/admin-cli-onboarding.html) |
| Orchestrator supervision loop | [wiki/concepts/orchestrator-supervision-loop.html](wiki/concepts/orchestrator-supervision-loop.html) |
| Runner stability: incidents, invariants & goal | [wiki/concepts/runner-stability-incidents.html](wiki/concepts/runner-stability-incidents.html) |
| Process termination & abort scenarios (test suite) | [wiki/concepts/process-termination-scenarios.html](wiki/concepts/process-termination-scenarios.html) |
| Overnight session summary 2026-06-23 (Zwischenstand) | [wiki/concepts/overnight-2026-06-23-summary.html](wiki/concepts/overnight-2026-06-23-summary.html) |
| claude.exe mid-run termination — live investigation | [wiki/concepts/claude-termination-investigation.html](wiki/concepts/claude-termination-investigation.html) |

## Organization Rules

- Keep source-of-truth contracts in `domains/`, `contracts/`, or
  `architecture/`.
- Keep dated evidence in `reports/`, `research/`, `frontend/audits/`, or
  `cli/audits/`.
- Keep explanatory and evolving notes in `wiki/`.
- Use Markdown by default.
- Use HTML only for visual or spatial pages that need layout, such as maps or
  reports. HTML pages must be self-contained and readable in the Wiki iframe.
- When adding a new document, put it in its real category and add it to the
  nearest category index.
