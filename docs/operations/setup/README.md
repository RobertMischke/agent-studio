# Setup guide

agent-orchestrator is a local Kanban board that drives your Claude Code, Codex, Copilot, or Gemini CLIs through a sequential task queue per watched project. The product pitch lives in [../../README.md](../../../README.md); the near-term direction lives in [../../ROADMAP.md](../../../ROADMAP.md). The hard rules every CLI driving the repo must follow are in [../../AGENTS.md](../../../AGENTS.md).

This folder is the **operator-facing setup guide**. [getting-started.md](./getting-started.md) is the full step-by-step walkthrough - start there. The other pages go deeper on one topic each: attaching a new project to the board, onboarding a new CLI agent, what a good first task looks like, and what to check when something is off.

## Pages

| File | Use it when |
|---|---|
| [getting-started.md](./getting-started.md) | First time setting up an instance at all: prerequisites, install, start, onboard a project, run a first task, troubleshooting quick list - start here. |
| [onboard-a-project.md](./onboard-a-project.md) | Product workflow for project creation through the UI or API, central task-store rules, runtime activation, and troubleshooting. |
| [onboard-an-agent-cli.md](./onboard-an-agent-cli.md) | A new CLI (Claude, Codex, Copilot, Gemini) needs to be installed and made auto-runnable on this machine. Includes the load-bearing **Codex on Windows sandbox quirk**. |
| [your-first-task.md](./your-first-task.md) | First time using the board on a new project: what to queue, how to watch it run, what counts as a good first task vs. an anti-pattern. |
| [troubleshooting.md](./troubleshooting.md) | FAQ-style: "agent only shows sandbox errors", "auto-mode flipped to manual", "two jobs in 3-progress", "header counters look wrong". |
| [linux-runner-host.md](./linux-runner-host.md) | Running a task on a remote Linux host with the standalone `agent-runner` (RM-5): provision, configure, and drive one task end-to-end via the Task Server API. |
| [remote-runner-persistent-connection.md](./remote-runner-persistent-connection.md) | Unattended remote operation: keep the SSH tunnel to the Task Server up as a supervised, auto-reconnecting service (autossh/systemd or a Windows scheduled task) and use the runner's `--health-check`. |
| [presentation-capture.md](./presentation-capture.md) | Regenerating deterministic presentation stills and recording safe silent loops or narrated backup footage against the ADR-0056 demo workspace. |

## Related references

- [../skills-architecture.md](../../concepts/skills-architecture.md) - portable-skills doctrine (the `.agents/skills/` library).
- [../../.agents/skills/task-api/SKILL.md](../../../.agents/skills/task-api/SKILL.md) - programmatic task creation / move via the HTTP API.
- [../cli-skills/README.md](../../system/cli/skills/README.md) - per-CLI deep references (frame model, session model, known incidents).
- [../agent-task-contract.md](../../system/contracts/agent-task.md) - the app-owned task lifecycle every watched project inherits.
