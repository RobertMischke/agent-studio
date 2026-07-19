# Portable Skills Architecture

## Summary

agent-orchestrator treats skills as portable workspace knowledge, not as a feature owned by one CLI. The canonical skill library lives in the task processor, while each watched project exposes a small, predictable lookup section in its README or agent instructions. The orchestrator can then attach skills during managed task runs, and direct CLI work in VS Code can still discover the same skills through the project lookup document.

External background reference: https://gemini.google.com/share/3d87737b325d

Note: the shared Gemini report may require sign-in. Keep the link as source context, but keep the durable architecture reasoning in this repository.

## Problem

The task processor is the preferred work surface, but users also work directly with Codex, Claude Code, Copilot, or Gemini from VS Code and other shells. A skill system that only works inside orchestrated runs would create two worlds:

- Managed taskboard runs know about reusable specialist workflows.
- Direct CLI sessions lose that knowledge unless the user remembers paths manually.

That split would make skills feel fragile and would discourage using the orchestrator as the central place for long-lived working knowledge.

## Decision

Use a two-layer model:

1. **Central skill library in agent-orchestrator.** General reusable skills live with the task processor and can be selected, suggested, and attached to managed task runs.
2. **Project skill lookup contract in watched projects.** Each child project has a README or agent-instruction section that points agents to the relevant central skills and any project-specific skills managed by the task processor.

The orchestrator owns skill selection for managed task runs. The project README owns discoverability for direct CLI sessions.

## Skill Types

### Standard Skills

Standard skills ship with the task processor and solve recurring cross-project problems:

- UI and screenshot analysis.
- Security review.
- Backend API change workflow.
- Angular frontend workflow.
- Playwright visual verification.
- Log, token, and quota analysis.
- Release preparation.

These skills are optional. They should never replace core lifecycle rules.

### Project-Specific Skills

Project-specific skills are created and managed in the task processor, but apply to one or more watched projects. Examples:

- "Runbook PWA constraints."
- "agent-orchestrator CLI driver rules."
- "Project-specific security model."
- "Preferred screenshot review style for this app."

They should still be visible through the watched project's README lookup section so direct CLI work can find them.

## What Stays Core

The following must remain deterministic runtime behavior or always-included core prompt material:

- Selecting the next ready task.
- Enforcing one active task per project.
- Moving task folders between states.
- Writing or regenerating `status.md`.
- Recording CLI output and session events.
- Applying auto-commit on configured review transitions.
- Respecting `RootPath`, `RepositoryPath`, job folder, and attachment/result paths.
- Preventing agents from moving jobs, editing queue state, or starting other tasks.

Skills may explain how to perform a workflow, but they must not own these rules.

## Project README Lookup Contract

Every watched project should expose a small section with a stable heading. The first naive version can be this:

> **Naming note (2026-05-13 rename):** the literal heading below is the
> wire-level contract checked by `backend/Features/Runtime/SkillReadinessService.cs`.
> It still reads "Agent Software Studio Skills" because the backend lookup
> has not been migrated to the new product name yet. Do not change the
> literal heading in watched projects until the backend lookup is updated
> in lock-step; otherwise readiness checks will fail.

```markdown
## Agent Software Studio Skills

This project is managed by agent-orchestrator (skill heading kept as the documented contract until the backend lookup is updated).

Core task lifecycle rules live in the task processor and are applied during managed task runs.

When working directly in a CLI, use these skill references:

- Standard skills:
  - `<task-processor-root>/.agents/skills/playwright-visual-verification/SKILL.md`
  - `<task-processor-root>/.agents/skills/security-review/SKILL.md`
- Project skills:
  - `<task-processor-root>/.agents/projects/<project-key>/skills/<skill-name>/SKILL.md`

Do not move `.orchestrator` job folders or edit task state manually.
```

This lookup section is deliberately plain Markdown. Every CLI can read it. Native CLI skill systems may later consume exported copies, but the README contract is the common denominator.

## Orchestrator Discovery And Attachment

For managed task runs, the prompt stack should be explicit:

1. Always-included core prompt.
2. Project context from the watched project.
3. Task prompt and job evidence paths.
4. Explicitly selected skills.
5. Automatically suggested skills only after user confirmation, at least in the first version.

For project validation, the orchestrator should use the same wiki-backed documentation flow as the rest of project context:

1. User opens a watched project.
2. User opens the project Wiki or Agent Docs gateway.
3. Orchestrator reads the skill guidance page and the linked agent instruction gateway.
4. Orchestrator reports wiki drift or missing gateway links as normal documentation findings.
5. If the guidance is missing or stale, user can create a normal agent task to update the wiki page.

This keeps the product honest: the orchestrator checks the documented project contract and uses the same task pipeline to fix it, without a separate skill-readiness UI.

## Storage Shape

Proposed canonical structure:

```text
.agents/
  core/
    AGENT_CORE.md
  skills/
    security-review/
      SKILL.md
      scripts/
      references/
    playwright-visual-verification/
      SKILL.md
      scripts/
      references/
    runtime-log-analysis/
      SKILL.md
      scripts/
      references/
      tests/
  projects/
    <project-key>/
      skills/
        <project-skill>/
          SKILL.md
```

The existing `docs/system/cli/skills/` files are an earlier, narrower version of this idea. They can be migrated into `.agents/skills/cli-*` later, with compatibility links left behind.

The `runtime-log-analysis` skill is the canonical example of a read-only analysis skill that pairs with a runtime evidence stream. Its per-report contract specialises [`docs/system/reports/analysis-reports.md`](../system/reports/analysis-reports.md) for one topic (`runtime-observability`); follow that pattern when adding analysis skills against new evidence streams.

## Risks

- **Prompt bloat.** Attach short skill excerpts first; link to full references.
- **Unclear activation.** Show attached and suggested skills in the UI.
- **Duplicate rules.** Core owns "must"; skills own "how".
- **Stale project wiki lookup.** Keep the canonical skill lookup in the project wiki and route AGENTS/README files there instead of duplicating skill detail.
- **Unsafe scripts.** Skills may include scripts, but script execution should be explicit, local, and reviewable.
- **Native CLI mismatch.** Treat native CLI skill exports as adapters, not as the source of truth.

## First Product Step

Start with project-level wiki-backed skill guidance:

- Keep the durable skill contract in the project wiki.
- Let Agent Docs act as a gateway to that wiki material.
- Avoid a separate "Check skill readiness" product surface; skill upkeep should be handled as normal wiki maintenance and drift work.

This can be built after the project detail screen exists.

### v1 implementation

The first naive readiness flow exists as legacy code, but it is no longer mounted in Agent Docs or the Project Hub. Skills are managed through the wiki-backed guidance flow instead:

- Backend: [`backend/Features/Runtime/SkillReadinessService.cs`](../../backend/Features/Runtime/SkillReadinessService.cs) parses `README.md`, `AGENTS.md`, and `.github/copilot-instructions.md` for an H2/H3 heading whose title contains "skill" plus four required phrases (`standardSkills`, `projectSkills`, `skillsPath`, `processorReference`). Verdicts are `pass` (heading + every phrase), `warning` (heading + at least one missing phrase), `fail` (no heading). The check is deterministic and never delegates to an LLM.
- Endpoints (under [`backend/Features/Runtime/SkillReadinessEndpoints.cs`](../../backend/Features/Runtime/SkillReadinessEndpoints.cs)):
  - `GET /api/projects/{name}/skill-readiness` returns the verdict.
  - `GET /api/projects/{name}/skill-readiness/fix-task-preview` returns the title + prompt the fix path would queue.
  - `POST /api/projects/{name}/skill-readiness/fix-task` queues a normal `2-ready` task whose prompt embeds the canonical lookup snippet. The watched project's source tree is **never** edited from the endpoint - the agent updates the README through the regular pipeline.
- Frontend: the legacy `project-skill-readiness-section` button/modal is intentionally not mounted. Agent Docs stays a document browser; skill guidance belongs in wiki pages and normal wiki drift maintenance.
- Tests: [`backend.Tests/SkillReadinessServiceTests.cs`](../../backend.Tests/SkillReadinessServiceTests.cs) pins the parser matrix, the fail / warning / pass verdicts, and the invariant that `CreateFixTask` queues a `2-ready` task without writing into the watched project.
