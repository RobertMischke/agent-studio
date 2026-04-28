# AGENTS.md

> **Single source of truth for agent instructions.** Read natively by Codex CLI, Claude Code, and the GitHub Copilot coding agent. The files [.github/copilot-instructions.md](.github/copilot-instructions.md) and [CLAUDE.md](CLAUDE.md) are 3-line compatibility shims that point here. Frontend-scoped rules live in [frontend/AGENTS.md](frontend/AGENTS.md) and apply only to changes under `frontend/`.

## Project Overview

Agent-Taskboard is a local AI work monitor: a .NET 10 backend plus an Angular 21 frontend that watches external job folders and displays agent progress as a Kanban board.

Keep the product boundary clear:
- This repository contains the taskboard app source code, prompts, and docs.
- Job folders live in watched target projects under `.orchestrator/jobs/`.
- The app observes external jobs; it should not store runtime job artifacts in this repository.

## Documentation Language

All written artifacts in this repository (README, AGENTS.md, docs/, prompts, code comments, commit messages, PR descriptions) are written in **English**. Chat conversation with the user may happen in any language, but anything you commit or write to disk in this repo stays English.

## Product Goal & Non-Goals

The taskboard drives a **sequential pipeline of tasks per project**. Parallelism exists across projects, never within one. Treat this as a hard product boundary when proposing or implementing changes.

In scope:
- Sequential, automated task execution **within a single project** — tasks queued on that project's board are picked up and processed one after another, automatically, without per-task human kick-off.
- **Parallelism across projects** — different watched projects (different watch paths) run their own pipelines independently and may execute concurrently.
- A single running target app per project on a single branch (typically `main`, occasionally a feature branch).
- Minimum overhead — the product exists precisely to avoid intra-project parallel-execution bookkeeping.

Out of scope (do not add, even if asked offhandedly):
- **Intra-project parallelism.** At most one task runs per project at any time. No fan-out across agents, machines, or branches inside one project.
- **Workspaces / workflows.** No multi-step workflow engine, no per-task workspace creation.
- **Branch orchestration.** The app does not create, switch, sync, or merge git branches. No worktrees. No branch-per-task.

If a request implies any of the out-of-scope items, surface the conflict to the user before implementing.

## Architecture

| Layer | Path | Notes |
|-------|------|-------|
| Backend API | `backend/` | ASP.NET Core, runs on `http://localhost:5030`, SignalR hub for live push. |
| Backend tests | `backend.Tests/` | xUnit. |
| Frontend | `frontend/` | Angular 21 standalone components, signals state, PWA, runs on `http://localhost:4010`. |
| E2E tests | `frontend/e2e/` | Playwright. See [frontend/e2e/README.md](frontend/e2e/README.md). |
| Filesystem contract | `docs/filesystem-contract.md` | Job folder layout. |
| Orchestrator prompt | `docs/autopilot-prompt.md` | Canonical workflow copied into watched targets. |
| Repo prompts | `.github/prompts/` | Reusable prompt templates. |
| Backend lifecycle | `api.sh` | start / stop / restart / status (sh — agents must use this). |

### Service & data layout (backend)

- `Services/Cli/` — one driver per CLI: `ClaudeCliService`, `CodexCliService`, `CopilotCliService`, `GeminiCliService`, all extending `CliExecutionServiceBase` (except Copilot, which predates the base class). `CliRouter` picks the right one by `cliType`. The contract every driver must satisfy is documented in [docs/supported-clis.md](docs/supported-clis.md).
- `Services/Cli/SessionRegistry.cs` — discovers sessions on disk and builds the `/api/cli/usage` report.
- `Services/Quota/*QuotaProbe.cs` — per-CLI quota probes. `QuotaService` aggregates and serves `/api/cli/quota` (with background refresh).
- `Services/Pty/` — PTY-based slash-command probes (used for parsing `/usage`, `/status`).
- `Models/` — DTOs: `JobInfo`, `JobDetail`, `CliExecution`, `CreateJobRequest`, `StartJobRequest`, etc.
- `Endpoints/JobEndpoints.cs` — all routes. Read here first when wiring a new feature.

### Key REST endpoints

| Method | Path | Purpose |
|--------|------|---------|
| GET | `/api/jobs` | List all jobs (flat). |
| GET | `/api/jobs/grouped` | Jobs grouped by state. |
| GET | `/api/jobs/{jobId}?watchPath=...` | One `JobDetail` (info + prompt + status + log). |
| POST | `/api/jobs` | Create job (`CreateJobRequest`). |
| POST | `/api/jobs/{jobId}/start?watchPath=...` | Start CLI execution. |
| POST | `/api/jobs/{jobId}/stop?watchPath=...` | Cancel running execution. |
| POST | `/api/jobs/{jobId}/continue?watchPath=...` | Resume with new prompt (same session). |
| GET | `/api/jobs/{jobId}/output?watchPath=...` | CLI stdout/stderr buffer. |
| GET | `/api/cli/usage` | Sessions + versions for all CLIs. |
| GET | `/api/cli/quota` | Per-CLI quota windows (used%, reset times). |
| GET | `/api/cli/{cliType}/models` | Model catalog for one CLI. |
| GET | `/api/watch-paths` | Configured workspaces. |

`jobId` (URL slug) + `watchPath` (project root) is the addressing scheme — `jobKey` is `watchPath::jobId` and is used internally only.

### Watched workspaces

Default dev configuration watches:
- `C:\Projects\agent-taskboard-workspace\projects\agent-taskboard` (this app's own task folders)
- `C:\Projects\agent-taskboard-workspace\projects\runbook`

Use `/api/watch-paths` to enumerate at runtime — never hardcode paths in tests; read them from there.

_(Shell policy, Backend Control, Frontend Control, and Build/Test/Verify are documented below under "Shell policy — sh, not PowerShell".)_

### Visual & behavioural changes — Playwright is mandatory

**Default = always test. Never ask the user whether to run Playwright or write a spec — just do it, with priority.** Asking ("should I add a spec?", "want me to verify?") is a regression. Treat the test + screenshot deliverable as part of the task itself.

After **every change with visual or behavioural impact** in the frontend (layout, styling, component templates, interaction states, new buttons, new flows), you must:

1. Run the relevant Playwright spec(s) under `frontend/e2e/` and confirm they pass.
2. If the change isn't covered by an existing spec, **add or extend one** before declaring the task done. Regression tests are the deliverable, not an afterthought.
3. **Show screenshots in the chat reply**, not just in the report. Capture before/after (or locked/unlocked, error/empty/loaded — whatever states the change introduces) and attach them inline. The user explicitly wants to see them on every visual or behavioural change. "It passes" is not enough; the user must see what the change looks like.
4. For changes that touch CLI execution paths (Claude / Codex / Copilot), run `claude-hello-world.spec.ts` (or the equivalent for the affected CLI) end-to-end. It is `@billable` — uses real quota — but cheap (one Haiku call, ~10s).

Skip Playwright only if the change is provably non-visual and non-behavioural (pure rename, comment edit, dependency bump with no API surface change). Document the reason in the PR description if you skip. **Do not ask before skipping; decide, document, move on.**

The full E2E setup, conventions, and authoring rules live in [frontend/e2e/README.md](frontend/e2e/README.md).

## Windows Shell Compatibility

- **Default shell for agents is bash / sh** (Git Bash on Windows). Do not invoke PowerShell from agent commands.
- Prefer existing `.sh` scripts (`api.sh`) over inline shell snippets.
- If a task genuinely requires Windows-specific tooling (`tasklist`, `taskkill`, `netstat`), call those binaries directly from sh — do not wrap them in `powershell -c`.
- Avoid shell-specific file-creation syntax (PowerShell here-strings, `Out-File`, `Set-Content`); use `cat <<'EOF'`, `tee`, or the `Write` tool.

## Job Folder Contract

Each job folder contains:

- `job.json` — metadata (id, title, state, order, agent, cliType, model, sessionName).
- `prompt.md` — task description.
- `status.md` — processing protocol or log.
- `logs/` — optional log files (CLI stdout/stderr lives here as `cli-output.log`).

States:

```text
1-preparation -> 2-ready -> 3-progress -> 4-review -> 5-completed
```

Only jobs in `2-ready` or `3-progress` can be started via `/api/jobs/{id}/start`. New jobs default to `1-preparation`; the create endpoint accepts an optional `targetState` to land directly in `2-ready`.

See `docs/filesystem-contract.md` for full details.

## Code Conventions

- Frontend uses Angular signals for state.
- Frontend components are standalone; do not introduce NgModules.
- Keep the existing dark Catppuccin-inspired UI direction.
- Keep the detail view as a simple protocol view, without tabs or metrics grids unless the product direction changes.
- Prefer small, scoped changes and avoid rewriting unrelated code.

### Selectors in Playwright tests

Prefer `data-testid="..."` for stable test hooks. If a feature you're touching lacks one and a spec needs it, add it to the component rather than reaching for a CSS-class selector.

## Orchestrator Instructions

This repository uses the orchestrator pattern for dependent projects. The shared autopilot workflow is defined in `docs/autopilot-prompt.md`; treat it as the single source of truth.

When onboarding or resyncing a watched target project, use `.github/prompts/sync-target-instructions.prompt.md`. Target projects should receive an `AGENTS.md` with the orchestrator workflow. Add a lightweight `.github/copilot-instructions.md` only as a compatibility shim when that project still needs Copilot Chat repository instructions.
