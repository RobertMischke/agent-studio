---
description: "Synchronize agent instructions to a watched target project. Use when: the job folder contract, states, or agent task contract changed and dependent projects need updated instructions."
agent: "agent"
---

# Sync Target Instructions

Read the **shared agent task contract**:
- [agent-task-contract.md](../../docs/contracts/agent-task.md) - the application and CLI-agent ownership boundary

Then find the configured watch paths. Prefer `GET /api/watch-paths` when the backend is running because it includes runtime resolution. Otherwise read backend configuration:
- [appsettings.json](../../backend/appsettings.json)
- [appsettings.Development.json](../../backend/appsettings.Development.json)
- `backend/appsettings.Local.json` when it exists locally

For **each watch path**, use `WatchPaths[*].RootPath` as the target project root. The task folder itself may be resolved through `.orchestrator.yml` and `TaskRepository`, but target agent instructions belong in the project root where the CLI works.

Create or update an `AGENTS.md` in that project root. Copy the content from `docs/contracts/agent-task.md` into an `## Agent Task Contract` section. `AGENTS.md` is the **single source of truth** and is read natively by Codex CLI, Claude Code, and the GitHub Copilot coding agent.

For tools that historically use a different filename, write **lightweight compatibility shims** that point at `AGENTS.md` (do not duplicate the workflow content):

- `.github/copilot-instructions.md` - for GitHub Copilot Chat / Copilot in VS Code.
- `CLAUDE.md` - for Claude Code installations whose `/init` workflow or older versions still look for this filename.

Each shim should be 3 to 5 lines: explain that this file is a compatibility marker and link to `AGENTS.md`.

## Important
- Do NOT overwrite existing project-specific sections in the target project's `AGENTS.md`. Append or merge the task contract section.
- Mark the generated section with a clear header like `## Agent Task Contract` so it can be identified and updated later.
- Create the target `.github/` folder only when writing the Copilot compatibility shim.
- Use the **exact same content** from `docs/contracts/agent-task.md`; do not paraphrase or modify it.
- Skip a shim only if the target project explicitly does not use that tool.
