# Quality System - Concept Inventory and Wording

Status: design exploration. No naming committed.

This document is intentionally stricter than the first mockup iteration. The mockup is useful because it made the concepts visible, but the product should implement a narrow evidence loop first.

## 1. What Was Collected

Everything mentioned in the conversation that examines, measures, or reacts to a project or a task:

| # | Mentioned as | Scope | Trigger | Output | Read-only? |
|---|---|---|---|---|---|
| 1 | Security Audit | Project | Manual | Findings report | Yes |
| 2 | Quality Gate | unclear | unclear | Pass or warning | Yes |
| 3 | Code Review | Task | After task | Comments per file or line | Yes |
| 4 | Performance Review | Runtime | Manual, "start, click through" | Numbers vs threshold | No, exercises the app |
| 5 | Traceability Check | Project and Task | Both | Findings | Yes |
| 6 | Skill, for example "generate tasks" | n/a | When invoked | New work | No, produces work |

The list mixes four genuinely different things. Keeping them separate is the main design job.

## 2. Clean Axes

| | Examines artifacts | Exercises running system | Produces work |
|---|---|---|---|
| Task scope | Code Check, Traceability Check on diff, Security Check on diff | Rare | Task-finder skill |
| Project scope | Security Audit, Architecture Audit, Traceability Audit | Performance Probe | Project-finder skill |

Skills do not grade work. They generate or transform work. They sit outside the review axes.

## 3. Recommended Vocabulary

Use the natural word for each different thing:

- **Project Audits** - project-scope, read-only, holistic. Examples: Security Audit, Architecture Drift Audit, Traceability Audit.
- **Task Checks** - task-scope, read-only, diff-focused. Examples: Code Check, Security Check on diff, Test Coverage Delta.
- **Performance Probes** - runtime measurements that exercise the app or backend. Examples: Startup Latency, Board Poll Roundtrip, Longtask Budget.
- **Skills** - reusable workflows that produce or transform work. Examples: Rewrite Task Prompt, Generate Follow-up Tasks, ADR Writer.

Avoid "Quality" as a primary UI destination for now. It is acceptable as an internal shorthand, but the product surface should use concrete labels.

## 4. Product Stance

The first version is evidence-first, not enforcement-first.

- Task Checks produce findings and review chips.
- Task Checks do not block `3-progress -> 4-review` in the first cut.
- Findings can be acknowledged by the reviewer or turned into normal queued tasks.
- Security findings should be visually prominent, but still review evidence.
- No automatic fixing from a finding.

This keeps the app aligned with the sequential queue. The user reviews and decides. The board does not become a hidden workflow engine.

## 5. Per-concept Summary

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

### Skills

- Reusable workflows that produce work.
- Portable across managed taskboard runs and direct CLI sessions.
- Already defined conceptually in [docs/skills-architecture.md](../../skills-architecture.md).
- Not the same thing as Audits, Task Checks, or Probes.

## 6. Storage Shape

Definitions live in the app library as versioned Markdown with frontmatter.

Runtime results live where the evidence belongs:

- Project Audit reports belong in the watched project, under project docs or project evidence.
- Task Check results belong in the job folder.
- Probe results belong with project diagnostics or a project evidence history.

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

## 7. Execution Mode for Task Checks

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

## 8. Security Promotion

Security is a review dimension, but the project page should treat it as special.

The prototype should keep:

- A featured Security panel near the top of project detail.
- Empty-state pressure when no security baseline exists.
- A compact security badge on project rows.
- Review history and evidence links.

This is not a duplicate of the definitions library. It is a project-state view over the same Security Audit evidence.

## 9. Skill Repository

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

## 10. Implementation Order

1. Security baseline panel and review history.
2. Review definition model for Project Audits and Task Checks.
3. Per-project Task Check defaults.
4. One spawned Task Check after task completion, writing findings into the job folder.
5. Findings on the review surface plus follow-up-task creation.
6. Local Skills catalog for installed workflows.
7. Probe slots and later actual Performance Probes.

## 11. Open Questions

1. Final UI label for the definitions library: "Review definitions", "Audits and Checks", or "Project checks"?
2. Exact storage for project audit reports: under `docs/security/reviews/`, under `docs/quality/reviews/`, or under a project evidence folder?
3. Whether spawned Task Checks run before the folder move to `4-review` or immediately after it. Either way, first version should not block the transition.
4. Whether injected checks are worth shipping at all in the first version.
