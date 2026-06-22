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

## Load-Bearing Entry Points

| Topic | Start here |
|---|---|
| Runner | [domains/runner.md](domains/runner.md) |
| Pipeline | [domains/pipeline.md](domains/pipeline.md) |
| Tasks | [domains/tasks.md](domains/tasks.md) |
| Frontend | [domains/frontend.md](domains/frontend.md) |
| Frontend navigation style guide | [frontend/style-guide/navigation.md](frontend/style-guide/navigation.md) |
| CLI | [domains/cli.md](domains/cli.md) |
| Tokens | [domains/tokens.md](domains/tokens.md) |
| ADR archive | [architecture/decisions/adr-archive.md](architecture/decisions/adr-archive.md) |
| Architecture model | [architecture/model.md](architecture/model.md) |
| Agent message bus | [architecture/bus/agent-message-bus.md](architecture/bus/agent-message-bus.md) |
| UX doctrine | [product/design-principles.md](product/design-principles.md) |
| Wiki classification | [product/wiki-document-classification.md](product/wiki-document-classification.md) |
| Wiki editing flow | [product/wiki-editing-and-branch-flow.md](product/wiki-editing-and-branch-flow.md) |
| Wiki document companion schema | [schemas/wiki-document-companion.schema.json](schemas/wiki-document-companion.schema.json) |
| Wiki drift audit | [meta/reports/wiki-drift-audit-2026-06-11.html](meta/reports/wiki-drift-audit-2026-06-11.html) |
| Supported CLIs | [cli/supported-clis.md](cli/supported-clis.md) |
| Setup | [operations/setup/README.md](operations/setup/README.md) |
| Common problems | [wiki/common-problems/README.md](wiki/common-problems/README.md) |
| API project identity / watchPath | [wiki/concepts/api-project-identity-and-watchpath.md](wiki/concepts/api-project-identity-and-watchpath.md) |
| Orchestrator drive-to-conclusion & CLI-crash resilience | [wiki/concepts/orchestrator-drive-to-conclusion.html](wiki/concepts/orchestrator-drive-to-conclusion.html) |
| Task integration & worktree/merge workflow | [wiki/concepts/task-integration-and-merge-workflow.md](wiki/concepts/task-integration-and-merge-workflow.md) |
| Merge config analysis (parallelism coupling) | [wiki/concepts/task-integration-merge-config-analysis.html](wiki/concepts/task-integration-merge-config-analysis.html) |
| Auto-review reissue / evidence-gate analysis | [wiki/concepts/auto-review-evidence-gate-analysis.html](wiki/concepts/auto-review-evidence-gate-analysis.html) |
| Planning & Research task type | [wiki/concepts/planning-research-task-type.html](wiki/concepts/planning-research-task-type.html) |
| Runtime prompt usage audit | [wiki/concepts/runtime-prompt-usage-audit.html](wiki/concepts/runtime-prompt-usage-audit.html) |
| Admin CLI onboarding | [wiki/concepts/admin-cli-onboarding.html](wiki/concepts/admin-cli-onboarding.html) |
| Orchestrator supervision loop | [wiki/concepts/orchestrator-supervision-loop.html](wiki/concepts/orchestrator-supervision-loop.html) |
| Runner stability: incidents, invariants & goal | [wiki/concepts/runner-stability-incidents.html](wiki/concepts/runner-stability-incidents.html) |

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
