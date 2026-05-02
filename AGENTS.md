# AGENTS.md

> **Single source of truth for agent instructions.** Read natively by Codex CLI, Claude Code, and the GitHub Copilot coding agent. The files [.github/copilot-instructions.md](.github/copilot-instructions.md) and [CLAUDE.md](CLAUDE.md) are 3-line compatibility shims that point here. Frontend-scoped rules live in [frontend/AGENTS.md](frontend/AGENTS.md) and apply only to changes under `frontend/`.

## Project Overview

Agent Task Processor is a local AI work monitor: a .NET 10 backend plus an Angular 21 frontend that watches external job folders and displays agent progress as a Kanban board.

For product context, read [README.md](README.md) and [ROADMAP.md](ROADMAP.md). The README explains what the tool is and how it is wired. The roadmap explains the product thesis, near-term themes, hard boundaries, and decision principles.

## Edit only the dev checkout

This repository is checked out twice in the parent `agent-taskboard-devspace/` folder: `agent-taskboard-dev/` (active development) and `agent-taskboard-stable/` (reference). **All edits go to the dev checkout.** Stable receives changes via `git pull` from `main` and is never edited directly.

The dev checkout marks itself visually (orange "DEV" stripe, orange PWA icon, `(DEV)` window title) when `backend/appsettings.Local.json` contains `{ "Environment": { "IsDev": true } }`. That file is gitignored, so the same source tree renders un-marked on stable.

Keep the product boundary clear:
- This repository contains the task processor app source code, prompts, and docs.
- Job folders live in watched target projects under `.orchestrator/jobs/`.
- The app observes external jobs; it should not store runtime job artifacts in this repository.

## Documentation Language

All written artifacts in this repository (README, ROADMAP.md, AGENTS.md, docs/, prompts, code comments, commit messages, PR descriptions) are written in **English**. Chat conversation with the user may happen in any language, but anything you commit or write to disk in this repo stays English.

Generated repository text must not use em dashes. Prefer a normal hyphen, a comma, a semicolon, or a new sentence.

User-facing application strings (UI labels, button text, banner copy, backend error messages surfaced to the UI) are **English** as well, even when the user dictates them in another language. Existing German strings are legacy and may be migrated opportunistically when you're already touching the surrounding code, but never introduce new non-English strings.

## Product Goal & Non-Goals

The task processor drives a **sequential pipeline of tasks per project**. Parallelism exists across projects, never within one. Treat this as a hard product boundary when proposing or implementing changes.

In scope:
- Sequential, automated task execution **within a single project**. Tasks queued on that project's board are picked up and processed one after another, automatically, without per-task human kick-off.
- **Parallelism across projects**. Different watched projects (different watch paths) run their own pipelines independently and may execute concurrently.
- A single running target app per project on a single branch (typically `main`, occasionally a feature branch).
- Minimum overhead. The product exists precisely to avoid intra-project parallel-execution bookkeeping.

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
| Protocol & image style | `docs/protocol-style.md` | `status.md` shape, Activity Log markers, `attachments/` vs `results/`, per-CLI image retention. |
| Agent task contract | `docs/agent-task-contract.md` | App-owned lifecycle boundary copied into watched targets. |
| Product roadmap | `ROADMAP.md` | Product thesis, roadmap themes, hard boundaries, and decision principles. |
| Repo prompts | `.github/prompts/` | Reusable prompt templates. |
| Runtime prompts | `prompts/runtime/` | Editable Markdown templates rendered by backend runtime services. |
| Backend lifecycle | `api.sh` | start / stop / restart / status (sh; agents must use this). |

### Orchestration philosophy: deterministic over prompt-based

The product treats orchestrator-to-CLI communication as a core capability. What the agent says about its own run is one input among several; the orchestrator is a deterministic arbiter, not a passive logger.

Three layers, each with its own pure-function library and test matrix:

1. `AgentOutcomeAnalyzer` (in [backend/Services/Runner/AgentOutcomeAnalyzer.cs](backend/Services/Runner/AgentOutcomeAnalyzer.cs)) parses the run's output buffer for hard sentinels (`[[TASK_DONE]]`, `[[TASK_BLOCKED:...]]`, `[[TASK_NEEDS_INPUT:...]]`, `[[TASK_NOOP]]`). Sentinel matches are authoritative. When no sentinel matches, the analyzer falls back to a heuristic and sets `MatchedSentinel = false` so the next layer can warn.
2. `RunOutcomePolicy` (in [backend/Services/Runner/RunOutcomePolicy.cs](backend/Services/Runner/RunOutcomePolicy.cs)) maps `(intent, plan, outcome, follow-up, retry)` to a typed `OutcomeAction`. The load-bearing rule: when the agent reports a fast Done or NoOp on a `UserContinue` that carried a follow-up, the orchestrator re-issues the work itself with stronger framing once, then stops and asks the user. Heuristic verdicts always surface as a meta message.
3. `OrchestratorChatLog` (in [backend/Services/Runner/OrchestratorChatLog.cs](backend/Services/Runner/OrchestratorChatLog.cs)) writes typed orchestrator messages (`decision`, `reissue`, `heuristic`, `giveup`) into `logs/cli-output.log` on the `[orchestrator]` stream. The frontend's activity-log parser renders them as a separate participant alongside `You` and the agent.

When you change a CLI driver, prompt template, or the runner's post-run path, keep these three layers in mind. The agent contract that backs the sentinel grammar lives in [docs/agent-task-contract.md](docs/agent-task-contract.md); when you add or change a sentinel, update both that file and `AgentOutcomeAnalyzer.SentinelRegex`.

### Service & data layout (backend)

- `Services/Cli/`: one driver per CLI: `ClaudeCliService`, `CodexCliService`, `CopilotCliService`, `GeminiCliService`, all extending `CliExecutionServiceBase` (except Copilot, which predates the base class). `CliRouter` picks the right one by `cliType`. The contract every driver must satisfy is documented in [docs/supported-clis.md](docs/supported-clis.md). **When you touch any of these files, also read the matching skill in [docs/cli-skills/](docs/cli-skills/) — [cli-overview](docs/cli-skills/cli-overview.md) plus the per-CLI skill ([cli-claude](docs/cli-skills/cli-claude.md), [cli-codex](docs/cli-skills/cli-codex.md), [cli-copilot](docs/cli-skills/cli-copilot.md), [cli-gemini](docs/cli-skills/cli-gemini.md)). The skills hold the operational knowledge that doesn't fit in code comments — frame catalogues, capture flows, known incidents, common-task playbooks. This is a hard rule for every CLI driving this repo (Claude Code, Codex, Copilot, Gemini): if the task touches a CLI driver, the matching skill is required reading before any code change. The pickup is enforced by two tests — a free scaffolding lock in [`backend.Tests/CliSkillFilesTests.cs`](backend.Tests/CliSkillFilesTests.cs) and a `@billable` live test in [`frontend/e2e/cli-skills-pickup.spec.ts`](frontend/e2e/cli-skills-pickup.spec.ts) that drives each CLI through the task processor and asserts it can echo back the sentinel string from the matching skill.**
- `Services/Cli/SessionRegistry.cs`: discovers sessions on disk and builds the `/api/cli/usage` report.
- `Services/Quota/*QuotaProbe.cs`: per-CLI quota probes. `QuotaService` aggregates and serves `/api/cli/quota` (with background refresh).
- `Services/Pty/`: PTY-based slash-command probes (used for parsing `/usage`, `/status`).
- `Models/`: DTOs: `JobInfo`, `JobDetail`, `CliExecution`, `CreateJobRequest`, `StartJobRequest`, etc.
- `Endpoints/JobEndpoints.cs`: all routes. Read here first when wiring a new feature.

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

`jobId` (URL slug) + `watchPath` (project root) is the addressing scheme. `jobKey` is `watchPath::jobId` and is used internally only.

### Watched workspaces

Local watch configuration usually lives in gitignored `backend/appsettings.Local.json`. Use `/api/watch-paths` to enumerate effective watch paths at runtime; it includes pointer resolution through `.orchestrator.yml` and `TaskRepository`. Never hardcode paths in tests; read them from there.

_(Shell policy, Backend Control, Frontend Control, and Build/Test/Verify are documented below under "Shell policy: sh, not PowerShell".)_

### Visual & behavioural changes: Playwright is mandatory

**Default = always test. Never ask the user whether to run Playwright or write a spec. Just do it, with priority.** Asking ("should I add a spec?", "want me to verify?") is a regression. Treat the test + screenshot deliverable as part of the task itself.

After **every change with visual or behavioural impact** in the frontend (layout, styling, component templates, interaction states, new buttons, new flows), you must:

1. Run the relevant Playwright spec(s) under `frontend/e2e/` and confirm they pass.
2. If the change isn't covered by an existing spec, **add or extend one** before declaring the task done. Regression tests are the deliverable, not an afterthought.
3. **Show screenshots in the chat reply**, not just in the report. Capture before/after or locked/unlocked, error/empty/loaded, or whatever states the change introduces, and attach them inline. The user explicitly wants to see them on every visual or behavioural change. "It passes" is not enough; the user must see what the change looks like.
4. For changes that touch CLI execution paths (Claude / Codex / Copilot), run `claude-hello-world.spec.ts` (or the equivalent for the affected CLI) end-to-end. It is `@billable`, uses real quota, and is cheap (one Haiku call, ~10s).

Skip Playwright only if the change is provably non-visual and non-behavioural (pure rename, comment edit, dependency bump with no API surface change). Document the reason in the PR description if you skip. **Do not ask before skipping; decide, document, move on.**

Playwright's `test-results/` folder is **scratch**. It is overwritten on every run and gitignored. Any screenshot that should survive past the next test run must be copied into the relevant `<job>/results/` folder so it ends up next to the Activity Log. The full image lifecycle (per-CLI retention rules, `attachments/` vs `results/`, how the protocol pane resolves them) lives in [docs/protocol-style.md](docs/protocol-style.md). Read it before changing anything image-related.

The full E2E setup, conventions, and authoring rules live in [frontend/e2e/README.md](frontend/e2e/README.md).

## Windows Shell Compatibility

- **Default shell for agents is bash / sh** (Git Bash on Windows). Do not invoke PowerShell from agent commands.
- Prefer existing `.sh` scripts (`api.sh`) over inline shell snippets.
- If a task genuinely requires Windows-specific tooling (`tasklist`, `taskkill`, `netstat`), call those binaries directly from sh. Do not wrap them in `powershell -c`.
- Avoid shell-specific file-creation syntax (PowerShell here-strings, `Out-File`, `Set-Content`); use `cat <<'EOF'`, `tee`, or the `Write` tool.

## Job Folder Contract

Each job folder contains:

- `job.json`: metadata (id, title, state, order, agent, cliType, model, sessionName).
- `prompt.md`: task description.
- `status.md`: generated review protocol.
- `logs/`: optional log files (CLI stdout/stderr lives here as `cli-output.log`).

States:

```text
1-preparation -> 2-ready -> 3-progress -> 4-review -> 5-completed -> 6-archive
```

Only jobs in `2-ready` or `3-progress` can be started via `/api/jobs/{id}/start`. New jobs default to `1-preparation`; the create endpoint accepts an optional `targetState` to land directly in `2-ready`.

Successful CLI runs move from `3-progress` to `4-review` through application code. Failed or stopped runs stay in `3-progress` for inspection, restart, or continuation.

See `docs/filesystem-contract.md` for full details.

## Code Conventions

- Frontend uses Angular signals for state.
- Frontend components are standalone; do not introduce NgModules.
- Keep the existing dark Catppuccin-inspired UI direction.
- Keep the detail view as a simple protocol view, without tabs or metrics grids unless the product direction changes.
- Prefer small, scoped changes and avoid rewriting unrelated code.

### Selectors in Playwright tests

Prefer `data-testid="..."` for stable test hooks. If a feature you're touching lacks one and a spec needs it, add it to the component rather than reaching for a CSS-class selector.

## Watched Target Instructions

This repository uses an app-owned task lifecycle for dependent projects. The shared boundary is defined in [docs/agent-task-contract.md](docs/agent-task-contract.md); treat it as the single source of truth for what the application controls and what the CLI agent controls.

After any CLI-executed task finishes, check whether [README.md](README.md), [ROADMAP.md](ROADMAP.md), AGENTS.md, or docs need to be updated. Update them in the same task when the change affects product direction, public behavior, architecture, CLI contracts, filesystem contracts, or agent workflow. If no documentation update is needed, say so briefly in the task report.

When onboarding or resyncing a watched target project, use [.github/prompts/sync-target-instructions.prompt.md](.github/prompts/sync-target-instructions.prompt.md). Target projects should receive an `AGENTS.md` with the agent task contract. Add a lightweight `.github/copilot-instructions.md` only as a compatibility shim when that project still needs Copilot Chat repository instructions.
