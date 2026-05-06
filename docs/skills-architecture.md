# Portable Skills Architecture

## Summary

Agent Software Studio treats skills as portable workspace knowledge, not as a feature owned by one CLI. The canonical skill library lives in the task processor, while each watched project exposes a small, predictable lookup section in its README or agent instructions. The orchestrator can then attach skills during managed task runs, and direct CLI work in VS Code can still discover the same skills through the project lookup document.

External background reference: https://gemini.google.com/share/3d87737b325d

Note: the shared Gemini report may require sign-in. Keep the link as source context, but keep the durable architecture reasoning in this repository.

## Problem

The task processor is the preferred work surface, but users also work directly with Codex, Claude Code, Copilot, or Gemini from VS Code and other shells. A skill system that only works inside orchestrated runs would create two worlds:

- Managed taskboard runs know about reusable specialist workflows.
- Direct CLI sessions lose that knowledge unless the user remembers paths manually.

That split would make skills feel fragile and would discourage using the orchestrator as the central place for long-lived working knowledge.

## Decision

Use a two-layer model:

1. **Central skill library in Agent Software Studio.** General reusable skills live with the task processor and can be selected, suggested, and attached to managed task runs.
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
- "Agent Software Studio CLI driver rules."
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

```markdown
## Agent Software Studio Skills

This project is managed by Agent Software Studio.

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

For project validation, the orchestrator should provide a simple project-level action:

1. User opens a watched project.
2. User clicks "Check skill readiness".
3. Orchestrator reads the project's README or agent instruction file.
4. Orchestrator reports whether the skill lookup section exists and matches the expected shape.
5. If it is missing or stale, user can create a normal agent task to add or update it.

This keeps the product honest: the orchestrator checks the project contract and uses the same task pipeline to fix it.

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
  projects/
    <project-key>/
      skills/
        <project-skill>/
          SKILL.md
```

The existing `docs/cli-skills/` files are an earlier, narrower version of this idea. They can be migrated into `.agents/skills/cli-*` later, with compatibility links left behind.

## Risks

- **Prompt bloat.** Attach short skill excerpts first; link to full references.
- **Unclear activation.** Show attached and suggested skills in the UI.
- **Duplicate rules.** Core owns "must"; skills own "how".
- **Stale project README lookup.** Add the project-level check and a task generator.
- **Unsafe scripts.** Skills may include scripts, but script execution should be explicit, local, and reviewable.
- **Native CLI mismatch.** Treat native CLI skill exports as adapters, not as the source of truth.

## First Product Step

Start with project-level skill readiness:

- Add a project action button.
- Show a modal with README lookup status.
- Offer to create a task that updates the selected project to the skill lookup contract.

This can be built after the project detail screen exists.
