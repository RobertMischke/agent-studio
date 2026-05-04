# Quality System - Integrated Concept Inventory

Status: design exploration. No naming committed.

This document keeps the quality-system and creative-design concepts in one product model. The app should expose several concrete project surfaces, not separate mockup families.

## 1. What Was Collected

Everything mentioned in the conversation that examines, measures, designs, or reacts to a project or a task:

| # | Mentioned as | Scope | Trigger | Output | Primary surface |
|---|---|---|---|---|---|
| 1 | Security Audit | Project | Manual | Findings report | Security |
| 2 | Architecture Audit | Project | Manual | Drift report | Architecture |
| 3 | Code Review | Task | After task or manual | Comments per file or line | Audits and Checks |
| 4 | Performance Review | Runtime | Manual | Numbers vs threshold | Test Quality |
| 5 | Traceability Check | Project and Task | Manual or after task | Findings | Audits and Checks |
| 6 | Design Loop | Project and Task | Manual | Screenshots, critique, follow-up task | UX/UI |
| 7 | Council Review | Project and Task | Manual | Role-separated critique | UX/UI |
| 8 | Backend / E2E / Tuning Tests | Project and Task | Manual | Test report, metrics, artifacts | Test Quality |
| 9 | Source-code Metrics | Project | Manual | LOC, modules, coverage, hotspots | Test Quality |
| 10 | Token Usage | Project, job, run | Observed and opened manually | Totals, heatmap, timeline | Token Usage |
| 11 | Skill, for example "generate tasks" | n/a | Manual action | New work or evidence | Skills |

## 2. Clean Axes

| | Examines artifacts | Exercises running system | Produces work or direction | Measures spend |
|---|---|---|---|---|
| Task scope | Code Check, Security Check on diff, Traceability Check | E2E run for task, tuning repro | Design next version, generate follow-up task | Job token drill-down |
| Project scope | Security Audit, Architecture Audit, Traceability Audit | Performance Probe, QA suite | Council review, design memory update, source map | Project token heatmap |

Skills are the action mechanism. They do not replace the surfaces. A Skill may power a UX/UI action, a QA action, an audit, or a source-map action, but the user finds the result on the relevant project surface.

## 3. Recommended Project Surfaces

- **Security** - project baseline, review history, active security risks.
- **Architecture** - ADRs, architecture notes, drift status.
- **UX/UI** - design references, screenshots, markdown briefs, images, accepted examples, rejected alternatives, council critique, design memory, next-version actions.
- **Test Quality** - backend tests, end-to-end tests, tuning tests, coverage, run history, source maps, lines of code, modules, dependencies, ownership areas, hotspots.
- **Token Usage** - total token spend, Job Tokens, Supporting Jobs Tokens, Orchestrator Tokens, heatmap, timeline, expensive jobs, job drill-down.
- **Audits and Checks** - review definitions, project audits, task checks, runtime probe slots.
- **Skills** - reusable workflows that are invoked by buttons from the surfaces above.

Avoid "Quality" as a primary UI destination for now. It is acceptable as an internal shorthand, but the product surface should use concrete labels.

## 4. Vocabulary

- **Project Audits** - project-scope, read-only, holistic. Examples: Security Audit, Architecture Drift Audit, Traceability Audit.
- **Task Checks** - task-scope, read-only, diff-focused. Examples: Code Check, Security Check on diff, Test Coverage Delta.
- **Performance Probes** - runtime measurements that exercise the app or backend. Examples: Startup Latency, Board Poll Roundtrip, Longtask Budget.
- **Design Loops** - explicit iterations that compare references, screenshots, critique, and next-version decisions.
- **Council Reviews** - structured critique passes with roles such as Product, Visual Design, Interaction Design, Frontend Engineering, Accessibility, and Marketing / Positioning.
- **QA Runs** - backend, end-to-end, tuning, coverage, and code-metric actions with artifacts and history.
- **Source-code Perspective** - a generated map of modules, lines of code, dependencies, ownership areas, coverage, and hotspots.
- **Token Usage** - observed inference spend, split into Job Tokens, Supporting Jobs Tokens, and Orchestrator Tokens.
- **Skills** - reusable workflows that produce work, evidence, reports, or analysis when explicitly invoked.

## 5. Product Stance

The first version is evidence-first, not enforcement-first.

- Task Checks produce findings and review chips.
- Task Checks do not block `3-progress -> 4-review` in the first version.
- Findings can be acknowledged by the reviewer or turned into normal queued tasks.
- Security findings should be visually prominent, but still review evidence.
- Design councils are advisory. The orchestrator or user decides whether to accept, iterate, or create follow-up tasks.
- QA and source-map actions are explicit. The app may suggest them, but the user triggers them unless a later project setting opts into safe automation.
- Token usage must be visible enough to change behavior, especially on large boards, but it is not an automatic scheduler.

This keeps the app aligned with the sequential queue. The user reviews and decides. The board does not become a hidden workflow engine.

## 6. Per-concept Summary

### Project Audits

- Big read-only examinations.
- Manual trigger from the project view.
- Long runs are acceptable.
- Output is a Markdown report plus structured findings.
- Findings can become normal queued tasks.
- Examples: `SEC-OVERVIEW`, `ARCH-DRIFT`, `TRACEABILITY-COVERAGE`.

### Task Checks

- Small per-task reviews.
- Configured in three layers: library, project default, per-task override.
- Run after the main task run finishes, or manually from review.
- Output warning chips and structured findings.
- Severity is informational in the first version.
- Examples: `CODE-CHECK`, `TRACEABILITY-DIFF`, `SEC-DIFF`, `TEST-COVERAGE-DELTA`.

### Performance Probes

- Exercise the running app or backend and measure.
- Manual trigger first, possible scheduling later.
- Output numbers, thresholds, history, and evidence.
- Built on existing primitives in [frontend/e2e/helpers/timing.ts](../../../frontend/e2e/helpers/timing.ts).
- Examples: `STARTUP-LATENCY`, `BOARD-POLL-ROUNDTRIP`, `LONGTASK-BUDGET`.

### UX/UI

- Holds references, screenshots, markdown briefs, images, accepted examples, rejected alternatives, product notes, and design memory.
- Offers explicit actions such as Run Screenshot Critique, Run Council Review, Request Next Version, and Create Follow-up Task.
- Preserves the chosen direction and rejected alternatives as evidence.
- Does not become a full design tool in the first version.

### Test Quality

- Holds backend tests, end-to-end tests, tuning tests, coverage, source-code metrics, module organization, and QA report history.
- Shows both raw artifacts and parsed metrics.
- Lets an LLM evaluate structured evidence, not invent pass/fail state from prose.
- Provides a source-code perspective for understanding software organization.

### Token Usage

- Aggregates inference spend by project, job, run, category, and time window.
- Splits spend into Job Tokens, Supporting Jobs Tokens, and Orchestrator Tokens.
- Uses totals, heatmaps, timelines, expensive-job lists, and drill-down.
- Makes supporting analysis loops visible, including QA, council, security, source map, and audit runs.

### Skills

- Reusable workflows that produce work, evidence, reports, or analysis.
- Portable across managed taskboard runs and direct CLI sessions.
- Already defined conceptually in [docs/skills-architecture.md](../../skills-architecture.md).
- Not the same thing as a UI surface. Skills power buttons; surfaces organize evidence.

## 7. Report Contracts

Definitions and outputs should be explicit about their parseable shape.

Human-facing reports can be Markdown, but they should also contain structured JSON for the app:

```json
{
  "kind": "test-run",
  "schemaVersion": 1,
  "status": "warn",
  "summary": "Backend tests passed; two Playwright specs failed.",
  "metrics": { "backendPassed": 142, "e2eFailed": 2, "coverage": 78.4 },
  "artifacts": ["results/qa/playwright-2026-05-04.md"]
}
```

If parsing fails:

- Show an "unstructured report" warning.
- Keep the raw Markdown visible.
- Do not hide evidence because the structured block was malformed.
- Do not infer metrics that were not present.

## 8. Storage Shape

Definitions live in the app library as versioned Markdown with frontmatter.

Runtime results live where the evidence belongs:

- Project Audit reports belong in the watched project, under project docs or project evidence.
- Task Check results belong in the job folder.
- Probe and QA results belong with project diagnostics or a project evidence history.
- UX/UI references and screenshots belong as project evidence or task evidence, depending on scope.
- Token usage is observed runtime metadata, stored in a queryable project/job/run history.

Proposed definition library:

```text
docs/quality/
  audits/
    SEC-OVERVIEW.md
    ARCH-DRIFT.md
    TRACEABILITY-COVERAGE.md
  checks/
    CODE-CHECK.md
    TRACEABILITY-DIFF.md
    SEC-DIFF.md
  probes/
    STARTUP-LATENCY.md
    BOARD-POLL-ROUNDTRIP.md
  skills/
    SCREENSHOT-CRITIQUE.md
    COUNCIL-REVIEW.md
    QA-RUNNER.md
    SOURCE-MAP.md
```

Example frontmatter:

```yaml
---
id: TRACEABILITY-DIFF
kind: check
title: Traceability on the task diff
dimension: traceability
severity: warn
executionMode: spawn
description: |
  Checks whether the diff adds error handling and timing where it touches
  new code paths.
instructions: |
  Read the task prompt, status, and changed files. Emit structured findings.
---
```

## 9. Execution Mode for Task Checks

A Task Check can be wired into a run in two fundamentally different ways.

### Mode A - Spawn

After the main task finishes, a fresh CLI invocation runs the check with its own prompt and context window.

Pros:

- Clean focused context.
- Higher signal-to-noise.
- Easier structured output.
- Independent retry and quota accounting.

Cons:

- Extra CLI invocation.
- Extra latency and token cost.
- Needs to reload enough context to review the diff.

### Mode B - Inject

The check instructions are added to the main task prompt. The same CLI run produces both the work and the self-check.

Pros:

- Nearly free.
- No extra invocation.
- The agent already has task context.

Cons:

- Less reliable.
- Output structure is weaker.
- Easy for long prompts to drown the check.
- Wrong default for security-sensitive checks.

Recommendation:

- Project Audits always spawn.
- Performance Probes are not agent checks.
- Security-sensitive Task Checks default to spawn.
- Cheap style or coverage reminders may inject.

## 10. Security Promotion

Security is a review dimension, but the project page should treat it as special.

The prototype should keep:

- A featured Security panel near the top of project detail.
- Empty-state pressure when no security baseline exists.
- A compact security badge on project rows.
- Review history and evidence links.

This is not a duplicate of the definitions library. It is a project-state view over the same Security Audit evidence.

## 11. Skill Repository

Repository-style discovery is attractive but not first-cut work.

First build:

- Local installed skills.
- Built-in workflow skills.
- License and source metadata.
- Project README lookup.
- Controlled installation path.

Later:

- Curated repository.
- Search and filtering.
- Explicit install with license confirmation.
- No hidden auto-update.
- No direct internet fetch from the UI.

The mockup may show repository entries as a future preview, but installation should be visually framed as "later" until local skill mechanics are stable.

## 12. Implementation Order

1. Security baseline panel and review history.
2. UX/UI, Test Quality, and Token Usage project menu surfaces beside Security and Architecture.
3. Review definition model for Project Audits and Task Checks.
4. Per-project Task Check defaults.
5. One spawned Task Check after task completion, writing findings into the job folder.
6. UX/UI evidence format for screenshots, references, council notes, accepted direction, and rejected alternatives.
7. QA run history for backend tests, end-to-end tests, tuning tests, coverage, and code metrics.
8. Source-code map action that visualizes modules, lines of code, ownership areas, coverage, dependencies, and organization concerns.
9. Token Usage surface with totals, category split, heatmap, timeline, expensive-job list, and drill-down.
10. Findings on the review surface plus follow-up-task creation.
11. Local Skills catalog for installed workflows.

## 13. Open Questions

1. Final UI label for the definitions library: "Review definitions", "Audits and Checks", or "Project checks"?
2. Exact storage for project audit reports: under `docs/security/reviews/`, under `docs/quality/reviews/`, or under a project evidence folder?
3. Whether spawned Task Checks run before the folder move to `4-review` or immediately after it. Either way, first version should not block the transition.
4. Whether injected checks are worth shipping at all in the first version.
5. Which token source is authoritative per CLI, and how much should be inferred when a CLI only exposes partial data?
6. Which source-code metrics are computed by scripts and which are judged by an LLM after the structured report exists?
