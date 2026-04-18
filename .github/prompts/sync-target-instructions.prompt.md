---
description: "Synchronize Copilot instructions to a watched target project. Use when: the job folder contract, states, or autopilot workflow changed and dependent projects need updated instructions."
agent: "agent"
---

# Sync Target Instructions

Read the **shared autopilot prompt template** (single source of truth):
- [autopilot-prompt.md](../../docs/autopilot-prompt.md) — the canonical orchestrator workflow

Then read the backend config to find all configured watch paths:
- [appsettings.Development.json](../../backend/appsettings.Development.json)

For **each watch path**, determine the parent project root (two levels above `.orchestrator/jobs/`).

Create or update a `.github/copilot-instructions.md` in that project root. Copy the content from `autopilot-prompt.md` into an `## Orchestrator — Autopilot Workflow` section.

## Important
- Do NOT overwrite existing non-orchestrator sections in the target project's `copilot-instructions.md`. Append or merge the orchestrator section.
- Mark the generated section with a clear header like `## Orchestrator — Autopilot Workflow` so it can be identified and updated later.
- If the target `.github/` folder doesn't exist, create it.
- Use the **exact same content** from `docs/autopilot-prompt.md` — do not paraphrase or modify it.
