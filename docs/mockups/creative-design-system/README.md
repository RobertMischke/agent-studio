# Creative Design System - Mockup

Design exploration. Goal: describe how Agent Task Processor can help produce beautiful software through design loops, references, screenshots, structured critique, tests, source-code metrics, and visible token usage.

This is a sibling to the quality-system mockup, not a replacement for it:

- Quality asks whether the work is correct, safe, and reviewable.
- Creativity and Design asks whether the work is coherent, expressive, usable, and worth shipping as product.
- Testing and QA asks whether the work keeps behaving correctly over time, with visible evidence from backend tests, end-to-end tests, tuning tests, coverage, and source-code metrics.
- Token Usage asks how much inference budget the project has spent, where it went, and which jobs deserve scrutiny.

## Files

- [taxonomy.md](taxonomy.md) - concepts, loop types, council roles, testing/QA surfaces, report contracts, storage shape, and implementation order.
- [ui.html](ui.html) - clickable dummy for project-level UX/UI, Test Quality, and Token Usage surfaces.

## Current Direction

Beautiful software needs a loop, not a single pass.

The app should support a design iteration cycle:

1. Define design intent.
2. Collect references: screenshots, markdown briefs, images, accepted examples, rejected examples.
3. Implement or mock a version.
4. Capture screenshots.
5. Run council-style critique.
6. Run targeted testing and QA actions when the user asks for them.
7. Let the orchestrator decide: accept, request another version, or create follow-up tasks.
8. Preserve the chosen direction and rejected alternatives as evidence.

This matters because a coding agent can produce a working UI that is still visually weak. The product should make it normal to ask for the next version, compare screenshots, and apply critical feedback before the task is accepted.

Design evidence is broader than screenshots. A project can carry reference images, product screenshots, markdown design briefs, UI principles, moodboards, and examples from earlier accepted tasks. These references should be visible and reusable from project and task surfaces, but they remain evidence and context, not hidden automatic instructions.

Testing and QA evidence belongs beside design evidence. Backend tests, end-to-end tests, tuning/performance tests, coverage, lines of code, module organization, dependency shape, and source-code maps should be visible as run history. The LLM can evaluate those reports, but it evaluates structured evidence. It should not invent pass/fail state from prose.

## Project-Level Menus

The project page should expose three dedicated surfaces:

- **UX/UI** - the place for design references, screenshots, markdown briefs, visual direction, council critique, design memory, and "next version" actions.
- **Test Quality** - the place for backend test runs, end-to-end test runs, tuning tests, coverage, source-code metrics, module organization, source maps, and QA report history.
- **Token Usage** - the place for total token spend, job-token heatmaps, timelines, and drill-down into which jobs, supporting jobs, and orchestrator turns consumed budget.

These are peer project dimensions beside Security and Architecture. They should not be buried under a generic Skills or Quality menu. Skills power the actions, but the user finds the results on the project surface.

Token Usage is especially important at scale. A board that has processed thousands of jobs needs more than a number in a corner. It needs a visual feeling for spend: small job squares, heat intensity, time windows, expensive outliers, and drill-down from project total to one job and its related supporting work.

## Council Concept

A Council is a structured critique pass with multiple roles. It is not parallel implementation.

Recommended first roles:

- Product: does the screen support the user's real workflow?
- Visual Design: does it look intentional, balanced, and polished?
- Interaction Design: are controls, states, and flows ergonomic?
- Frontend Engineering: is the design implementable without fragile tricks?
- Accessibility: can users with different needs operate it?
- Marketing and Positioning: does the screen carry the product story?

The orchestrator reads the council notes and chooses the next step. The council gives opinions; the orchestrator owns the decision.

## Action-Driven Skills

Creative design, testing, QA, and source analysis actions are button-driven. They do not happen everywhere automatically.

Examples:

- Run screenshot critique.
- Add design reference.
- Run council review.
- Run backend tests.
- Run end-to-end tests.
- Run tuning tests.
- Generate source map.
- Analyze module organization.
- Request next design version.

Each action invokes a Skill, prompt trigger, or script-backed workflow. The action writes structured evidence back to the task or project. The user sees the button, chooses the action, and can inspect the resulting report.

## Report Contracts

The first implementation should define report interfaces explicitly. A Skill may write Markdown for humans, but it should also emit a small structured block the app can parse.

Recommended shape:

```json
{
  "kind": "test-run",
  "schemaVersion": 1,
  "status": "pass",
  "summary": "Backend tests passed; two Playwright specs still skipped.",
  "metrics": { "passed": 142, "failed": 0, "coverage": 78.4 },
  "artifacts": ["results/qa/backend-tests-2026-05-04.md"]
}
```

If parsing fails, the UI must show the raw Markdown report with an "unstructured report" warning. The contract is an interface with graceful degradation, not a reason to hide evidence.

## Token Categories

Token Usage should split spend into at least three categories:

- **Job Tokens** - tokens used by the primary CLI run that implements or reviews the job.
- **Supporting Jobs Tokens** - tokens used by analysis, council, QA, design, security, or source-map runs attached to the job.
- **Orchestrator Tokens** - tokens used by the orchestrator or supervisor logic related to the job: steering, reissue, summaries, and decisions.

The project view should aggregate all three while preserving drill-down to each job, run, and category.

## Critical Boundaries

- Design loops may create screenshots, critique, design briefs, and follow-up tasks.
- Design loops must not start parallel coding tasks inside one project.
- Council output is evidence, not an automatic mandate.
- The first implementation should not build a full design tool.
- Generated or searched visual references are allowed only as task evidence or design inspiration, not as hidden product dependencies.
- Testing and QA actions are explicit. The app may suggest them, but the user triggers them unless a project later opts into a specific safe automation.
- Report parsing must be defensive. Broken JSON or missing fields produce visible warnings and raw report access.
- Source-code metrics are a perspective for understanding software, not a replacement for review.
- Token usage must be visible enough to change behavior. Expensive jobs, supporting loops, and orchestrator overhead should be obvious without opening a terminal.

## First Implementation Slice

1. Add UX/UI, Test Quality, and Token Usage as project-level menu surfaces.
2. Design evidence format for screenshot variants and council notes.
3. Design reference library for screenshots, markdown briefs, images, accepted examples, and rejected alternatives.
4. Screenshot comparison panel in task detail.
5. Local design Skills for screenshot critique, UI polish, copy tone, and accessibility design review.
6. Testing and QA run history for backend tests, end-to-end tests, tuning tests, coverage, and code metrics.
7. Source-code map action that visualizes modules, lines of code, ownership areas, and organization concerns.
8. Token Usage project surface with category totals, heatmap, timeline, and job drill-down.
9. Council review prompt with role-separated critique.
10. "Next version" action that creates a follow-up task from council feedback.
11. Project-level design memory for accepted visual direction and examples.

## What This Mockup Is For

This mockup should help future implementation tasks answer:

- What does a design loop produce?
- What does the council critique?
- What references should a design loop read?
- What belongs under UX/UI on a project?
- What belongs under Test Quality on a project?
- What belongs under Token Usage on a project?
- Which testing and QA actions are available?
- Which source-code metrics should be visualized?
- How should token spend be split between job, supporting jobs, and orchestrator categories?
- What does a parseable report contract look like?
- Which artifacts stay with the task?
- How does the orchestrator choose "next version"?
- How does the product stay sequential while still getting richer critique?
