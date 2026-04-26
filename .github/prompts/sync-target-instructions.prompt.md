---
description: "Synchronize agent instructions to a watched target project. Use when: the job folder contract, states, or autopilot workflow changed and dependent projects need updated instructions."
agent: "agent"
---

# Sync Target Instructions

Read the **shared autopilot prompt template** (single source of truth):
- [autopilot-prompt.md](../../docs/autopilot-prompt.md) — the canonical orchestrator workflow

Then read the backend config to find all configured watch paths:
- [appsettings.Development.json](../../backend/appsettings.Development.json)

For **each watch path**, determine the parent project root (two levels above `.orchestrator/jobs/`).

Create or update an `AGENTS.md` in that project root. Copy the content from `autopilot-prompt.md` into an `## Orchestrator — Autopilot Workflow` section. `AGENTS.md` is the **single source of truth** and is read natively by Codex CLI, Claude Code, and the GitHub Copilot coding agent.

For tools that historically use a different filename, write **lightweight compatibility shims** that point at `AGENTS.md` (do not duplicate the workflow content):

- `.github/copilot-instructions.md` — for GitHub Copilot Chat / Copilot in VS Code (still required by current Copilot Chat repository-instructions loader).
- `CLAUDE.md` — for Claude Code installations whose `/init` workflow or older versions still look for this filename.

Each shim should be ~3–5 lines: explain that this file is a compatibility marker and link to `AGENTS.md`.

## Important
- Do NOT overwrite existing non-orchestrator sections in the target project's `AGENTS.md`. Append or merge the orchestrator section.
- Mark the generated section with a clear header like `## Orchestrator — Autopilot Workflow` so it can be identified and updated later.
- Create the target `.github/` folder only when writing the Copilot compatibility shim.
- Use the **exact same content** from `docs/autopilot-prompt.md` — do not paraphrase or modify it.
- Skip a shim only if the target project explicitly does not use that tool.
