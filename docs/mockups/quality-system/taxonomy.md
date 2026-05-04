# Quality System — Concept Inventory & Wording

Status: design exploration. No naming committed.

## 1. What's been collected so far

Everything mentioned in the conversation that examines, measures, or reacts to the project or a task:

| # | Mentioned as | Scope | Trigger | Output | Read-only? |
|---|---|---|---|---|---|
| 1 | Security Audit | Project | Manual | Findings report | Yes (examines) |
| 2 | Quality Gate | unclear | unclear | Pass/Warn | Yes |
| 3 | Code Review | Task | After task | Comments per file/line | Yes |
| 4 | Performance Review | Runtime | Manual ("start, click through") | Numbers vs threshold | **No — exercises the app** |
| 5 | Traceability Check | Project + Task | Both | Findings | Yes |
| 6 | Skill (e.g. "generate tasks") | n/a | When invoked | New work | **No — produces work** |

The list mixes three genuinely different kinds of things. That's the source of the wording fog.

## 2. Two clean axes

| | Examines artefacts (read-only) | Exercises running system (live) | Produces work |
|---|---|---|---|
| **Task scope** | Code Review, Traceability Check on diff | (rare) | Task-finder skill |
| **Project scope** | Security Audit, Architecture Review, Traceability Audit | Performance Probe (start app, click through) | Project-finder skill |

Skills don't grade work, they generate it. They sit outside the quality axes.

## 3. Wording options

### Option A — "Reviews" umbrella

- **Reviews** (umbrella)
  - Project Reviews — big, manual, holistic
  - Task Reviews — small, auto on task completion
- **Probes** — live runtime measurements
- **Skills** — capabilities (separate)

**Pro**: "Review" is the gentlest word.
**Con**: clashes hard with the existing `4-review` task state. Confusing.

### Option B — "Audits" everywhere

- **Audits** — Project Audits + Task Audits
- **Probes** — runtime
- **Skills** — separate

**Pro**: user validated "Security Audit". Consistent.
**Con**: user explicitly flagged "Audit" can feel heavy for small per-task checks.

### Option C — "Checks" flat with attributes

Single concept "Check" with `scope: task | project | runtime` and `kind: security | quality | performance | traceability`.

**Pro**: minimal vocabulary.
**Con**: "Check" is bland. Loses the strong "Security Audit" framing the user liked. Forces UI filters to do the categorising work that words could do for free.

### Option D — Mixed, follow the natural words *(recommended)*

Three different words for three genuinely different things:

- **Audits** — project-scope, read-only, holistic. "Security Audit", "Architecture Audit", "Traceability Audit". Long-running, manual, weighty.
- **Task Checks** — task-scope, read-only, light. "Code Check", "Traceability Check on diff", "Test Coverage Delta". Run automatically at `3-progress -> 4-review`, also manual. Toggleable per task.
- **Performance Probes** — runtime, exercises the app. "Startup Latency", "Board Poll Roundtrip", "Longtask Budget". Manual.
- **Skills** — separate top-level concept. Reusable agent workflows that produce work.

**Pro**: matches the language the user already uses ("Security Audit, ja, ist geil!"). Each word does one job. UI labels read naturally.
**Con**: three concepts to learn instead of one.

## 4. Recommendation: Option D

The vocabulary follows what the user already says. "Audit" carries weight, "Check" is light, "Probe" implies live execution. Different words for different things. Skills stay unbundled because they generate work, not grades.

## 5. Per-concept summary (under Option D)

### Audits (project-scope)
- Big read-only examinations.
- Manual trigger from the project view. Long runs are fine.
- Output: report file (markdown) under `docs/audits/reviews/<date>-<audit-id>.md`. Findings can be turned into tasks via button.
- Examples: `SEC-OVERVIEW`, `ARCH-DRIFT`, `TRACEABILITY-COVERAGE`.

### Task Checks (task-scope)
- Small per-task verifications.
- Configured in three layers: library -> project default -> per-task toggle.
- Run automatically at `3-progress -> 4-review`, manual re-run any time.
- Output: warning chips on the task. Severity is informational; never blocks state transition.
- Examples: `CODE-CHECK`, `TRACEABILITY-DIFF`, `TEST-COVERAGE-DELTA`.

### Performance Probes (runtime)
- Exercise the running app (or backend) and measure.
- Manual trigger; later possibly scheduled.
- Output: numbers + threshold comparison. History over time.
- Built on the existing primitives in [frontend/e2e/helpers/timing.ts](../../../frontend/e2e/helpers/timing.ts).
- Examples: `STARTUP-LATENCY`, `BOARD-POLL-ROUNDTRIP`, `LONGTASK-BUDGET`.

### Skills (separate)
- Reusable agent workflows that produce work.
- Already partially defined under [docs/cli-skills/](../../cli-skills/) and [docs/skills-architecture.md](../../skills-architecture.md).
- Out of scope for this design — listed here only to make clear they are not the same thing.

## 6. Storage shape

One library directory, three subfolders by kind. Every definition is a markdown file with frontmatter.

```
docs/quality/
  audits/
    SEC-OVERVIEW.md
    ARCH-DRIFT.md
    TRACEABILITY-COVERAGE.md
  checks/
    CODE-CHECK.md
    TRACEABILITY-DIFF.md
  probes/
    STARTUP-LATENCY.md
    BOARD-POLL-ROUNDTRIP.md
  reviews/                 # all run outputs land here, dated
    2026-05-04-SEC-OVERVIEW.md
    2026-05-04-job-abc123-CODE-CHECK.md
```

Frontmatter shape (same for all kinds):

```yaml
---
id: TRACEABILITY-DIFF
kind: check          # audit | check | probe
title: Traceability on the task diff
dimension: traceability
severity: warn       # info | warn | high — informational, never blocks
description: |
  Checks the diff produced by this task adds error-handling and
  timing instrumentation where it touches new code paths.
check-instructions: |
  (prompt rendered for the agent)
---
```

## 7. New dimension: Execution Mode (Task Checks only)

A Task Check can be wired into the run in two fundamentally different ways. This is a real engineering decision per check, not a global setting, so it lives on the definition.

### Mode A &mdash; **Spawn** (separate CLI step)

After the main task finishes, a fresh CLI invocation runs the check with its own prompt and its own context window.

- **Pro**: clean, focused context. The check sees only what it needs (the diff, the task, the rule). Higher signal-to-noise; the check cannot be overshadowed by a long primary prompt. Easier to attribute failures to the check itself.
- **Pro**: independent retry, independent tokens, independent quota accounting.
- **Con**: extra CLI invocation per check &mdash; tokens, latency, quota cost.
- **Con**: the check has to re-load enough project context to do its job.

### Mode B &mdash; **Inject** (prompt addition)

The check's instructions are prepended/appended to the main task prompt. The same CLI run produces both the work and the self-check.

- **Pro**: zero extra CLI invocations. Nearly free.
- **Pro**: the agent is already loaded with full task context; no re-loading needed.
- **Con**: less reliable. The check competes with the main task for attention and context. Easy to forget to report, easy to drown in a long primary prompt.
- **Con**: harder to make the result structured. A spawned check can be required to emit `[[CHECK_RESULT: ...]]`; an injected one often can't.
- **Con**: less safe for high-severity checks (security on diff). A separate run is worth the cost when "the agent might have skipped this" is unacceptable.

### Recommendation per kind

- **Audits** (project-scope) &mdash; always Spawn. The whole point is a focused, reportable examination.
- **Probes** (runtime) &mdash; not applicable; probes execute the app, not the agent.
- **Task Checks** &mdash; mode is a per-definition decision. Default to **Spawn** for high-severity and security-relevant checks, **Inject** for cheap stylistic ones. The UI shows the mode on each card and on the definition detail so it's never surprising.

### Storage shape addition

```yaml
---
id: TRACEABILITY-DIFF
kind: check
executionMode: spawn   # spawn | inject
...
---
```

Project- and task-level override of the execution mode is a future extension; not in the first cut.

## 8. Open questions

1. Top-level navigation label for the section: "Quality" / "Quality System" / "Project Quality" / something else?
2. Should the library UI be one screen with kind-filter chips, or three separate screens (Audits / Checks / Probes)?
3. Do project-level audit results live in `docs/quality/reviews/` (versioned in git, PR-fähig) or in a per-project state file? Recommendation: versioned markdown.
4. Performance Probes — phase them in later or include in the first cut? Recommendation: define the slot in the UI, ship Audits + Checks first, leave Probes as "coming soon" tile.
