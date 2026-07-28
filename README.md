# agent-orchestrator

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

For a guided Linux x64 release install with no source checkout and no .NET
prerequisite, download
[`agent-orchestrator-setup`](https://github.com/agent-orc/agent-studio/releases/latest/download/agent-orchestrator-setup).
It offers an isolated Docker demo, a native single-machine install, and a
guided multi-machine join flow.

For development from source:

```bash
./api.sh
cd frontend
npm install
npm start
```

Or ask your coding-agent CLI to do it. For anything beyond the default setup, see the [setup guide](./docs/operations/setup/getting-started.md) and [AGENTS.md](AGENTS.md).

## More

For architecture, ADRs, contracts, and the full documentation index, see [agent-orchestrator.dev](https://agent-orchestrator.dev).
