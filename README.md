# agent-orchestrator

**Management layers on top of coding work.** Agents (Claude Code, Codex, GitHub Copilot, Gemini) write the code; this repository is the Studio: a task board, agent pipelines, and project wikis that assign, gate, review, and account for it.

<picture>
  <source media="(prefers-color-scheme: dark)" srcset="docs/media/architecture-dark.svg">
  <img alt="Architecture: a browser Studio and a central Task Server on one HTTPS origin (authority channel); Runner-Hosts execute on any host over outbound-only claim/lease/results channels; code travels separately over git origin." src="docs/media/architecture-light.svg" width="760">
</picture>

## What you see

![The board: task cards moving through backlog, active, and review lanes](docs/media/board.png)

![Task detail: pipeline steps, live agent activity, and run evidence](docs/media/task-detail.png)

## At a glance

- **Stack:** .NET backend, Angular frontend.
- **Runs:** Claude Code, Codex, GitHub Copilot, or Gemini CLIs as the coding engine.
- **Remote-first:** a central Task Server holds task state; Agent Runners execute on any host, outbound-only.
- **Every run leaves evidence:** structured events, artifacts, and diffs are captured per run and kept next to the code.

## Running locally

```bash
./api.sh
cd frontend
npm install
npm start
```

Or ask your coding-agent CLI to do it. For anything beyond the default setup, see the [setup guide](./docs/operations/setup/getting-started.md) and [AGENTS.md](AGENTS.md).

## More

For architecture, ADRs, contracts, and the full documentation index, see [agent-orchestrator.dev](https://agent-orchestrator.dev).
