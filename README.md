# Agent-Taskboard

**Local AI Work Monitor** — a standalone app (.NET 10 + Angular 21) that watches coding agents at work and renders their progress as a Kanban board.

> **Documentation language:** all repository documentation (README, AGENTS.md, docs/) is written in English. Conversation in chat may be in any language; written artifacts in this repo stay English.

## Product Goal

The taskboard drives a **sequential pipeline of tasks per project**. Multiple projects may run in parallel, but inside a single project work is strictly serial.

Concretely:

- **Sequential, automated task execution — per project.** Within one watched project, tasks queued on the board are picked up and worked through automatically, one after another. No human kick-off per task.
- **Parallelism only across projects, never within one.** At most one task is in flight per project at any time. Different projects (different watch paths) may execute their own pipelines concurrently and independently.
- **No workspaces, no workflows, no branch orchestration.** The app does not create or sync git branches, does not spin up worktrees, and does not manage multi-step workflows across environments.
- **One running app per project, one branch.** Each project assumes a single running target application on one branch (`main`, or occasionally a feature branch) and pushes its queue of tasks through that one environment.
- **Minimum overhead.** No branch synchronization, no merge coordination, no intra-project parallel-execution bookkeeping — that overhead is exactly what this product is designed to avoid.

If a future requirement implies parallel agents within a single project, multi-branch orchestration, or workspace management, that is **out of scope** for this product. Resist the temptation to add it.

## Concept Boundary (IMPORTANT)

```
┌─────────────────────────────┐     ┌──────────────────────────────────┐
│  agent-taskboard/           │     │  Target project (e.g. C:\Proj\X) │
│  ════════════════           │     │  ═══════════════════════════════  │
│  App source code:           │     │  Where the agent works:          │
│  - backend/ (.NET API)      │     │  - src/, lib/, ...               │
│  - frontend/ (Angular PWA)  │     │  - .orchestrator/                │
│  - docs/                    │     │    └── jobs/                     │
│  - .github/prompts/         │     │        ├── 1-preparation/        │
│                             │────>│        ├── 2-ready/              │
│  The app READS / WATCHES    │     │        ├── 3-progress/           │
│  the target's jobs/ folder. │     │        ├── 4-review/             │
│  It contains no jobs/.      │     │        └── 5-completed/          │
└─────────────────────────────┘     └──────────────────────────────────┘
```

### What lives where?

| Location | Contents |
|----------|----------|
| `agent-taskboard/` | App source code, prompts, docs |
| `<target-project>/.orchestrator/jobs/` | Job folders with `job.json`, `prompt.md`, `status.md` |

### Why this separation?

1. **The taskboard is a standalone product** — its source does not belong inside the projects it observes.
2. **Jobs belong to the target project** — the agent works there, so its artifacts live there.
3. **One taskboard, multiple targets** — a single taskboard can watch several target paths sequentially.
4. **Clean git history** — job artifacts do not pollute the taskboard source, and vice versa.

## Running

Agents must use the `sh` variant (`./api.sh`). The `.ps1` file remains for manual PowerShell sessions only — never invoke it from an agent.

```sh
# Backend (sh — works in Git Bash / WSL / any POSIX shell)
./api.sh start

# Frontend (VS Code task "Frontend: Start"
# or: npm start --prefix frontend)
```

## Configuration

```json
// backend/appsettings.json
{
  "WatchPaths": [
    "C:\\Projects\\Runbook\\App\\.orchestrator\\jobs"
  ]
}
```

## Keeping target projects in sync

When the workflow or folder schema changes, agent instructions in target projects must be updated. Use the `/sync-target-instructions` prompt in `.github/prompts/`.

## Docs

- [AGENTS.md](AGENTS.md) — canonical agent instructions
- [NEW-I.md](NEW-I.md) — initiative & mission
- [PATHS.md](PATHS.md) — path conventions
- [docs/filesystem-contract.md](docs/filesystem-contract.md) — job folder contract
