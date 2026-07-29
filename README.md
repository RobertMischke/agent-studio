# agent-orchestrator

[![Release build](https://github.com/agent-orc/agent-studio/actions/workflows/release.yml/badge.svg)](https://github.com/agent-orc/agent-studio/actions/workflows/release.yml)
[![License](https://img.shields.io/github/license/agent-orc/agent-studio)](LICENSE)

**Management layers on top of coding work.** Agents (Claude Code, Codex, GitHub Copilot, Gemini) write the code; this repository is the Studio: a task board, agent pipelines, and project wikis that assign, gate, review, and account for it.

<picture>
  <source media="(prefers-color-scheme: dark)" srcset="docs/media/architecture-dark.svg">
  <img alt="Architecture: a browser Studio and a central Task Server on one HTTPS origin (authority channel); Runner-Hosts execute on any host over outbound-only claim/lease/results channels; code travels separately over git origin." src="docs/media/architecture-light.svg" width="760">
</picture>

## What you see

![The board: task cards moving through backlog, active, and review lanes](docs/media/board.png)

![Task detail: pipeline steps, live agent activity, and run evidence](docs/media/task-detail.png)

## What it provides

- Batch remote and local coding-agent work with task management in one place.
- Reduces cognitive load: task state, progress and evidence are visible instead of remembered.
- A management layer over autonomous agent runs: assign, gate, review, account.
- Works with your existing CLI subscriptions, or bring your own API keys.
- Runs fully local or distributed, your choice per project. Project chat follows
  the project's execution runner and shows its host, repository checkout,
  branch, and revision.
- Keeps the Task Server as the durable control plane while separately fenced
  Remote Review Executors inspect immutable result revisions.
- Runs review decisions, council reactions, post-processing, gate dispatch, and
  completion judging in the separate API-only Orchestrator Engine. Flow
  definitions and in-flight runs remain durable Task Server data, so restarting
  the Engine does not orphan work.

## Running locally

Prerequisites are Windows with Git Bash, the .NET 10 SDK, Node.js 22, npm 11,
and at least one supported coding-agent CLI. From a fresh checkout:

```bash
git clone https://github.com/agent-orc/agent-studio.git agent-orchestrator
cd agent-orchestrator
cp backend/appsettings.Local.json.example backend/appsettings.Local.json

# Edit appsettings.Local.json: choose an empty TaskRepository, set
# Environment.IsDev to false, remove the Runner and DevTools blocks,
# and start with an empty WatchPaths array.
dotnet restore agent-taskboard.sln
npm ci --prefix frontend

ATP_ALLOW_DEV_BACKEND=1 ./api.sh start
npm start --prefix frontend
```

Open `http://localhost:4010`. For CLI onboarding, project registration, and
anything beyond this single-instance setup, follow the
[setup guide](./docs/operations/setup/getting-started.md). Contributors should
also read [AGENTS.md](AGENTS.md).

## More

Agent Studio is part of the agent-orc ecosystem. It uses
[Chat](https://github.com/agent-orc/chat) for coding-agent conversations and
sits alongside [Runner](https://github.com/agent-orc/runner) for hardened CLI
execution, [Token Economy](https://github.com/agent-orc/token-economy) for
model pricing and usage accounting, and
[Quality Studio](https://github.com/agent-orc/quality-studio) for layered code
review.

For future product direction, see the [roadmap](ROADMAP.md). For architecture,
ADRs, contracts, and the full documentation index, see
[agent-orchestrator.dev](https://agent-orchestrator.dev).

Licensed under the [Apache License 2.0](LICENSE).
