# Setup guide

agent-orchestrator is a local Kanban board that drives your Claude Code, Codex, Copilot, or Gemini CLIs through a sequential task queue per watched project. The product pitch lives in [../../README.md](../../../README.md); the near-term direction lives in [../../ROADMAP.md](../../../ROADMAP.md). The hard rules every CLI driving the repo must follow are in [../../AGENTS.md](../../../AGENTS.md).

This folder is the **operator-facing setup guide**: how to attach a new project to the board, how to onboard a new CLI agent, what a good first task looks like, and what to check when something is off. It complements [../getting-started.md](./getting-started.md), which is the bare install / run quickstart.

## Pages

| File | Use it when |
|---|---|
| [onboard-a-project.md](./onboard-a-project.md) | Adding a new watched project to the board (`WatchPaths` entry, backend restart, first lane bootstrap). |
| [onboard-an-agent-cli.md](./onboard-an-agent-cli.md) | A new CLI (Claude, Codex, Copilot, Gemini) needs to be installed and made auto-runnable on this machine. Includes the load-bearing **Codex on Windows sandbox quirk**. |
| [your-first-task.md](./your-first-task.md) | First time using the board on a new project: what to queue, how to watch it run, what counts as a good first task vs. an anti-pattern. |
| [troubleshooting.md](./troubleshooting.md) | FAQ-style: "agent only shows sandbox errors", "auto-mode flipped to manual", "two jobs in 3-progress", "header counters look wrong". |

## Related references

- [../getting-started.md](./getting-started.md) - manual install and configuration of the backend + frontend.
- [../skills-architecture.md](../../product/skills-architecture.md) - portable-skills doctrine (the `.agents/skills/` library).
- [../../.agents/skills/task-api/SKILL.md](../../../.agents/skills/task-api/SKILL.md) - programmatic task creation / move via the HTTP API.
- [../cli-skills/README.md](../../cli/skills/README.md) - per-CLI deep references (frame model, session model, known incidents).
- [../agent-task-contract.md](../../contracts/agent-task.md) - the app-owned task lifecycle every watched project inherits.
