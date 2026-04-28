# Agent Task Processor

**Local AI Work Monitor** — a standalone app (.NET 10 + Angular 21) that watches coding agents at work and renders their progress as a Kanban board.

> **Documentation language:** all repository documentation (README, AGENTS.md, docs/) is written in English. Conversation in chat may be in any language; written artifacts in this repo stay English.

## Product Goal

The task processor drives a **sequential pipeline of tasks per project**. Multiple projects may run in parallel, but inside a single project work is strictly serial.

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

1. **The task processor is a standalone product** — its source does not belong inside the projects it observes.
2. **Jobs belong to the target project** — the agent works there, so its artifacts live there.
3. **One task processor, multiple targets** — a single task processor can watch several target paths sequentially.
4. **Clean git history** — job artifacts do not pollute the task processor source, and vice versa.

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

## Supported CLIs

The task processor drives multiple coding-agent CLIs through a common interface. Today: Claude Code, Codex, GitHub Copilot, and Gemini (skeleton — Phase 1 in progress).

The contract every supported CLI must satisfy — process lifecycle, session model, model selection, quota probing, logging, cancellation — is documented in [docs/supported-clis.md](docs/supported-clis.md). Adding a new CLI follows the checklist in §4 of that file.

## Quality Gate for Agent Changes

Before any task is moved from `3-progress/` to `4-review/`, the agent runs the **Edge-Case Quality Gate** documented in [docs/autopilot-prompt.md](docs/autopilot-prompt.md#edge-case-quality-gate-mandatory-before-moving-to-4-review). It forces an explicit walk through:

- every runtime state the feature observes,
- whether the signal being checked is the same as the property being cared about (folder name vs. live process, etc.),
- crash / restart recovery behavior,
- failure UX (prefer disabled affordances over post-hoc error modals),
- reversibility of any state the change locks down.

The answers are recorded in the job's `status.md` under a `## Quality Gate` section. A job without that section is not ready for review.

## Docs

- [AGENTS.md](AGENTS.md) — canonical agent instructions
- [docs/supported-clis.md](docs/supported-clis.md) — CLI integration contract
- [NEW-I.md](NEW-I.md) — initiative & mission
- [PATHS.md](PATHS.md) — path conventions
- [docs/filesystem-contract.md](docs/filesystem-contract.md) — job folder contract
