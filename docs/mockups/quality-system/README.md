# Quality System - Integrated Mockup

Design exploration. **A click-dummy.** Goal: keep one coherent project-level mockup that shows review evidence, design loops, QA, source-code metrics, and token usage without splitting the product into competing concepts.

This folder is the only active mockup for this product direction. The earlier creative design exploration has been folded into this project shell.

## Files

- [taxonomy.md](taxonomy.md) - concept inventory, vocabulary, storage shape, report contracts, and implementation order.
- [ui.html](ui.html) - clickable dummy UI. Open in a browser. Catppuccin-ish dark to match the real frontend.

## Current Recommendation

Use concrete project surfaces instead of a vague top-level Quality product:

- **Security** on the project page, visually promoted.
- **Architecture** for ADRs, high-level architecture maps, and architecture review.
- **Drift** for project-level divergence between specs, tasks, jobs, ADRs, source code, README, AGENTS, marketing, tests, runtime behavior, and design references. The Drift view should include a marble-style architecture map with at most ten elements and per-element software drift.
- **UX/UI** for design references, screenshots, markdown briefs, accepted and rejected variants, council critique, and next-version actions.
- **Test Quality** for backend tests, end-to-end tests, tuning tests, coverage, source-code maps, module organization, and QA report history.
- **Token Usage** for project totals, category splits, job heatmaps, timelines, expensive jobs, and drill-down into job, supporting job, and orchestrator spend.
- **Steering Docs** for the README, AGENTS, task contract, skills lookup, ADR index, runtime prompt references, project-specific notes, human summaries, and drift warnings.
- **Audits and Checks** for configured review definitions and per-task evidence rules.
- **Skills** as reusable, explicitly triggered workflows that power actions on the surfaces above.

The mockup still has an internal `#/quality` route for the definitions library because it is a dummy. Product UI should call that surface "Review definitions" or "Audits and Checks" until the word "Quality" proves useful in real use.

## Action-Driven Principle

Design, QA, source analysis, audits, checks, and token drill-downs are explicit actions. They should not quietly run everywhere.

Examples:

- Run security audit.
- Run screenshot critique.
- Run council review.
- Request next design version.
- Run backend tests.
- Run end-to-end tests.
- Run tuning tests.
- Generate source map.
- Open token heatmap.
- Analyze project drift.
- Compare specs to tasks and jobs.
- Compare ADRs to source code.
- Compare architecture map elements to source code, tests, schemas, runtime behavior, and recent job evidence.
- Compare marketing and README to product behavior.
- Summarize steering docs.
- Check steering docs drift.
- Propose README or AGENTS update.
- Analyze recurring job-output failures.

Each action may invoke a Skill, prompt trigger, or script-backed workflow. The action writes evidence back to the project or task. The user sees the button, chooses the action, and can inspect the resulting report.

## Report Contracts

Reports should be human-readable Markdown plus a small structured block for the app. The app can parse the block for cards, badges, metrics, and heatmaps.

If parsing fails, the UI must show the raw Markdown report with an "unstructured report" warning. The contract is an interface with graceful degradation, not a reason to hide evidence.

## Critical Boundaries

This design must not turn the app into a workflow engine.

- A Task Check can create findings, chips, and follow-up task suggestions.
- A Task Check must not silently hold a task in `3-progress` in the first version.
- A spawned check is a separate CLI invocation and must still respect one active coding task per project.
- Design councils are advisory evidence, not automatic mandates.
- QA actions are explicit unless a project later opts into a specific safe automation.
- Token usage is visibility and accountability, not a scheduling policy by itself.
- Drift scores are triage signals with evidence links, not automatic decisions.
- Steering-doc analysis produces reviewable proposals, not hidden instruction edits.
- Follow-up work becomes a normal queued task.

## First Implementation Slice

1. Project Security panel with baseline state and review history.
2. Drift, UX/UI, Test Quality, and Token Usage as project-level menu surfaces beside Security and Architecture.
3. Review definition model for Audits and Task Checks, stored as Markdown with frontmatter.
4. Per-project Task Check defaults.
5. One spawned Task Check after a main task finishes, writing structured findings into the job folder.
6. Design evidence format for screenshot variants, references, council notes, and next-version decisions.
7. QA run history for backend tests, end-to-end tests, tuning tests, coverage, and source metrics.
8. Token usage aggregation with Job Tokens, Supporting Jobs Tokens, Orchestrator Tokens, heatmap, timeline, and job drill-down.
9. Drift report schema and scoring for intent, spec, task, job, architecture, documentation, marketing, design, test, runtime, process, schema, token, and per-architecture-element software drift.
10. Architecture model contract for a high-level map with at most ten elements.
11. Drift project surface with score cards, dimension history, marble architecture map, action buttons, and follow-up-task creation.
12. Steering Docs surface with raw agent-facing documents, human summaries, drift warnings, and evidence-backed update proposals.
13. Orchestrator output-pattern analysis that detects repeated failures across jobs and proposes steering-documentation improvements.
14. Findings visible on the task review surface with a "create follow-up task" action.
15. Local Skills catalog for installed workflow skills.

## What This Mockup Is For

When the next implementation cycle starts, this folder should answer:

- Which concepts are separate.
- Which project surfaces exist.
- Which actions are explicit.
- Which reports must be parseable.
- What not to build yet.
- Which terminology is safe enough for the UI.
- Where the roadmap intentionally narrows the design.

It is not the final design. It is a stricter draft that turns broad visual exploration into an implementable sequence.
