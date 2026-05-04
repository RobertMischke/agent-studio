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

## Architecture Decisions Archive

[docs/architecture-decisions.md](docs/architecture-decisions.md) holds the durable archive of **load-bearing** decisions: product boundaries, architectural philosophies, hard non-goals, and reasoning styles. The bar is high. Bug fixes, defensive guards, individual feature choices, and policy tweaks belong in commits and code comments, not in this file. If an entry would read like a changelog line, it does not belong there.

Two questions to use as the bar:

1. **Would a future contributor re-derive this the wrong way without it?** If yes, ADR. If the code + tests already make the decision obvious, no ADR.
2. **Does this say "we deliberately do not do X"?** Non-goals are the highest-value content here, especially patterns that look attractive but were ruled out.

When a chat lands on a decision that clears the bar, add or supersede an entry. Keep the surrounding narrative in sync: README and ROADMAP describe the current product shape, AGENTS describes how agents work in this repo, the ADR file explains the *why* and what was ruled out. Do not let those drift.

Do not over-archive. Most chat-driven changes are commits, not ADRs.

## Portable Skills

Reusable specialist workflows are **portable skills**, not CLI-local silos. The canonical design is documented in [docs/skills-architecture.md](docs/skills-architecture.md).

The rule of thumb:

- Core orchestration rules are always active and stay in code, runtime prompts, AGENTS.md, and the target task contract.
- Skills are optional, situational workflow guides. They explain how to do a specialist job; they must not own task lifecycle, state movement, review transitions, or queue policy.
- Agent Task Processor is the central home for standard skills and project-specific skills.
- Watched child projects should expose a small README or agent-instruction lookup section that tells direct CLI agents where to find the relevant skills.

This lookup section matters because users may work both through the orchestrator and directly in Codex, Claude Code, Copilot, or Gemini from VS Code. Managed taskboard runs can attach selected skills explicitly; direct CLI sessions rely on the watched project's README or AGENTS.md lookup section. Native CLI skill exports may come later, but the Markdown lookup is the shared baseline.

When changing skill behavior, keep [docs/architecture-decisions.md](docs/architecture-decisions.md) in sync if the change affects this boundary.

## Stable update policy

The dev checkout (`agent-taskboard-dev/`) and the stable checkout (`agent-taskboard-stable/`) sit side by side under `agent-taskboard-devspace/`. All edits go to dev. Stable receives changes via `update-stable.sh` in the parent folder, which `git pull --ff-only`s from `origin/main`, runs `npm install` if `package-lock.json` changed, and restarts stable. **Stable is never edited directly.**

When to update stable:

- **After a coherent batch ships and is verified in dev.** A "batch" is a feature plus its tests plus its documentation, all green. Single-commit speculative pushes to stable add risk for no gain.
- **Before a long unattended run.** If you are about to leave the orchestrator working on a board for hours, update stable first so the running version matches the documented behaviour. The Layer 3 system review monitor watches stable; running it against an out-of-date stable wastes the run.
- **After a load-bearing change to runner / supervisor / outcome-policy / agent contract.** These are observation surfaces; if dev and stable diverge on them, the activity log and supervisor logs disagree on what the system is actually doing.
- **Never mid-run.** `update-stable.sh` stops stable. If a job is in `3-progress` on stable, finish or stop it explicitly first.

When NOT to update stable:

- Dev work is unfinished or untested. Wait for the batch to settle.
- A change is purely exploratory (mockups under `docs/mockups/`, research under `docs/research/`). These do not affect runtime; they can sit in dev.
- The stable instance is currently being used to drive a long task. Wait for the task to finish or stop, then update.

The push step is its own decision. The default is to push only after the user has reviewed the dev commits and explicitly asks. Do not push silently as part of "wrapping up a batch"; the user owns that gate.

Layer 3 - the system review monitor at [`scripts/supervisor/run-system-review.sh`](scripts/supervisor/run-system-review.sh) - reads stable's state read-only. It is the most useful right after a stable update has happened: it can confirm the new code is running, capture a baseline, and surface any drift against the just-shipped behaviour.

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
| Design principles | `docs/design-principles.md` | UX contract for the abstraction layer over agents + software: top-level summary, always-available drill-down, run-as-unit-of-conversation. |
| Protocol & image style | `docs/protocol-style.md` | `status.md` shape, Activity Log markers, `attachments/` vs `results/`, per-CLI image retention. |
| Agent task contract | `docs/agent-task-contract.md` | App-owned lifecycle boundary copied into watched targets. |
| Portable skills architecture | `docs/skills-architecture.md` | Central skill library, project README lookup contract, and future project-level checks. |
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

### Multi-loop supervision (above the runner)

The runner described above is itself the second loop in a four-layer model. When you change supervisor code or wire a new check, keep the model and the load-bearing rules in mind:

1. **Layer 0** - the CLI agent's own loop (Claude / Codex / Copilot / Gemini). Owned by the vendor. The orchestrator can only start, observe, kill.
2. **Layer 1** - the orchestrator's job-pickup loop per project. The deterministic post-run policy (ADR-0002) decides outcomes here.
3. **Layer 2** - the per-project supervisor (`backend/Services/Supervisor/*`). Watches Layer 1 in real time. Default behaviour is cooperative signalling: writes typed `SupervisorAdvisory` records to `logs/meta/<project>/observations.jsonl`. Four pre-emptive primitives (`cancelRun`, `pausePickup`, `forceFail`, `resume`) exist for the rare emergency and route through the existing runner methods so the runner stays the single state-machine authority. Auto-intervention is a separate opt-in policy (`Supervisor:AutoInterventionEnabled`, default false). The full design rationale is in [`docs/research/orchestrator-meta-loop-analysis-2026-05-04.md`](docs/research/orchestrator-meta-loop-analysis-2026-05-04.md) and [ADR-0017](docs/architecture-decisions.md).
4. **Layer 3** - the external system review monitor (`scripts/supervisor/run-system-review.sh`). Stand-alone Claude session driven from outside the app, reads stable's state on a 4-8h cadence, writes a structured Markdown review under the workspace's `logs/system-review/`. Survives any failure mode of the app itself, including the Layer 2 supervisor.

Hard rules:
- The supervisor is **advice-first, force-rare**. Routine outcomes still flow through `RunOutcomePolicy`; the supervisor adds a kill-switch and a soft-reasoning second opinion, not a parallel orchestrator.
- **Single-writer state machine**: emergency primitives never poke job state directly; they call `TaskRunnerService.StopJob` / `SetMode` so there is exactly one cancel implementation.
- **Feedback-loop guard**: every event carries a `Source`. Auto-intervention and observation parsing never act on `Source: AutoIntervention` or `Source: User` events; they would feed back into the loop they came from.
- **Auto-intervention stays gated**: enabling it is a per-instance decision; the rate limit (`Supervisor:AutoInterventionRateLimit`, default 3/h/project) and severity threshold (`Supervisor:AutoInterventionSeverityThreshold`, default High) protect against runaway invocations.

### Service & data layout (backend)

- `Services/Cli/`: one driver per CLI: `ClaudeCliService`, `CodexCliService`, `CopilotCliService`, `GeminiCliService`, all extending `CliExecutionServiceBase` (except Copilot, which predates the base class). `CliRouter` picks the right one by `cliType`. The contract every driver must satisfy is documented in [docs/supported-clis.md](docs/supported-clis.md). **When you touch any of these files, also read the matching skill in [docs/cli-skills/](docs/cli-skills/) - [cli-overview](docs/cli-skills/cli-overview.md) plus the per-CLI skill ([cli-claude](docs/cli-skills/cli-claude.md), [cli-codex](docs/cli-skills/cli-codex.md), [cli-copilot](docs/cli-skills/cli-copilot.md), [cli-gemini](docs/cli-skills/cli-gemini.md)). The skills hold the operational knowledge that doesn't fit in code comments - frame catalogues, capture flows, known incidents, common-task playbooks. This is a hard rule for every CLI driving this repo (Claude Code, Codex, Copilot, Gemini): if the task touches a CLI driver, the matching skill is required reading before any code change. The pickup is enforced by two tests - a free scaffolding lock in [`backend.Tests/CliSkillFilesTests.cs`](backend.Tests/CliSkillFilesTests.cs) and a `@billable` live test in [`frontend/e2e/cli-skills-pickup.spec.ts`](frontend/e2e/cli-skills-pickup.spec.ts) that drives each CLI through the task processor and asserts it can echo back the sentinel string from the matching skill.**
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
| GET | `/api/jobs/{jobId}/runs?watchPath=...` | Per-job run timeline: ordered CLI invocations between user inputs + aggregates (RunCount, FirstStartedAt, LastActivityAt, HasActiveRun). Drives the protocol-pane run cards. |
| GET | `/api/jobs/{jobId}/runs/{index}/commits?watchPath=...` | Git commits whose author date falls in run #index's wall-clock window. Drives the per-run software-side change set. |
| GET | `/api/cli/usage` | Sessions + versions for all CLIs. |
| GET | `/api/cli/quota` | Per-CLI quota windows (used%, reset times). |
| GET | `/api/cli/{cliType}/models` | Model catalog for one CLI. |
| GET | `/api/watch-paths` | Configured workspaces. |

`jobId` (URL slug) + `watchPath` (project root) is the addressing scheme. `jobKey` is `watchPath::jobId` and is used internally only.

### Watched workspaces

Local watch configuration usually lives in gitignored `backend/appsettings.Local.json`. Use `/api/watch-paths` to enumerate effective watch paths at runtime; it includes pointer resolution through `.orchestrator.yml` and `TaskRepository`. Never hardcode paths in tests; read them from there.

_(Shell policy, Backend Control, Frontend Control, and Build/Test/Verify are documented below under "Shell policy: sh, not PowerShell".)_

### Regression-proofing: data, then five-whys, then test-then-fix

When the user reports a regression ("X is suddenly slow", "Y broke after Z"), the workflow is:

1. **Reproduce with measurement, not theory.** Time the slow path, profile the failing path, capture the bad output. State the numbers in the chat. Hypotheses without measurement are rejected on principle.
2. **Five whys against the data.** Walk back from the symptom to a single root cause. Each "why" must be backed by code or measurement, not by intuition. Stop when one more "why" would leave the codebase.
3. **Why didn't the existing test catch it?** Before writing the fix, name the gap in the test suite that let the regression ship. That gap is what the new test must close. If the answer is "we have no test for that whole layer," say so explicitly so the gap is visible.
4. **Write the regression test FIRST and prove it FAILS on the broken code.** A test that only passes after the fix proves the fix builds; a test that fails before and passes after proves the fix actually addresses the regression. Until the test fails on HEAD, the fix is speculative.
5. **Apply the fix and watch the same test go green.** No other change in the same commit unless it is required by the fix.
6. **Re-run the original measurement.** Numbers in the chat: before vs. after. The fix is not done until the user-visible metric is back in range.

Worked example: the auto-loop snapshot folded onto every JobInfo in `WithRuntime` made `/api/jobs/grouped` a 15-second call (frontend polls every 5 s, so the UI froze permanently). The diagnostic showed `loopMs ≈ 7800ms` per call dominating; the cause was `GetStuckLoopStateForJob` resolving the project via `JobScannerService.FindJob`, which performs a full disk rescan on every invocation. The regression test [`JobsEndpointPerfTests.WithRuntime_Over200Jobs_FinishesWellUnderOneSecond`](backend.Tests/JobsEndpointPerfTests.cs) builds a 200-job board, runs the overlay, asserts under 1 s. It failed at 19 s on the broken code and passed at well under 50 ms after the fix (look up by `ProjectName` against the in-memory `_runners` dictionary instead of re-scanning disk).

Endpoints that are polled by the UI carry an extra obligation: the perf test must reflect a realistic workload (≥ 150-200 jobs for the kanban endpoints) so a future O(N²) regression cannot hide behind a small fixture. New per-job overlay logic that calls back into a scanner method is a smell; review it on the way in.

#### When the symptom is in the UI, measure in the UI

The user's seat is the browser. A green API timing does not prove the UI is fast - change detection, computeds, blocking renders, and stacked polls all live above the API and the user feels them as lag the moment a single one regresses. **When the report mentions the UI ("Detail-Ansicht laggt", "Create dauert lang", "scrolling stutters"), the regression test belongs in [`frontend/e2e/`](frontend/e2e/), not in [`backend.Tests/`](backend.Tests/).**

Three Playwright primitives cover most cases, all CLI-friendly (no UI required), all collected as helpers in [`frontend/e2e/helpers/timing.ts`](frontend/e2e/helpers/timing.ts):

- **`apiRoundtrip(page, urlGlob, trigger)`** - times an outbound HTTP call from inside the running app via `page.waitForResponse`. Matches what the app's polling actually pays (HttpClient overhead + interceptors + browser queue), not what `curl` shows. Use for "polled endpoint stays under N ms from the browser's seat".
- **`startLongTaskRecorder(page)`** - installs a `PerformanceObserver` for `longtask` entries (browser definition: any main-thread block > 50 ms). Returns a callback that reads the running total. Use for "panel idle for 5 s does not block the main thread for more than X ms cumulatively". This is the metric that tracks scrolling smoothness.
- **`clickToVisible(trigger, target)`** - wall time between a click and the target locator becoming visible. Use for action latency: opening the detail panel, creating a job, expanding a card.

Other techniques in the toolbox when the basic three are not enough: `page.context().newCDPSession(page)` + `Performance.getMetrics` for ScriptDuration / LayoutDuration / JSHeapUsedSize; `Emulation.setCPUThrottlingRate` for worst-case CPU reproduction (do not enable in the default suite); `context.tracing.start({ snapshots, screenshots })` for the trace-viewer timeline as evidence on a failure.

**Gating rule for these specs:** never `await page.waitForLoadState('networkidle')` when the regression you are testing for is "the network never goes idle". Use `domcontentloaded` plus a short `waitForTimeout` to let the first poll fire, then assert with explicit numbers. Otherwise the test fails with a 15 s infrastructure timeout and hides the real latency reading from the report.

Worked example: when the user said the Detail-Ansicht laggt and Create dauert lang, the right test was [`frontend/e2e/perf-frontend.spec.ts`](frontend/e2e/perf-frontend.spec.ts). Reverting the backend fix made the grouped-jobs roundtrip test fail with `grouped jobs poll took 11521 ms from the browser` - that's the measurement, not a guess. Re-applying the fix turned it green at well under 1 s.

### Prompt-template changes: live probe required

Any change to a file under `prompts/runtime/` (the runner bootstrap templates, the summary template, the commit-message template) is a behavioral change against the agent CLI, not a textual change. Unit tests on rendered string content cannot catch a regression in how the CLI reacts to the new wording or structure - that lesson is recorded in [ADR-0007](docs/architecture-decisions.md). Before claiming a prompt change is safe, run the `@billable` `claude-hello-world.spec.ts` (or the equivalent for the affected CLI) end-to-end and confirm the agent produces real output, not a fast "I'll wait for your request" exit. The structural unit-test guards in [backend.Tests/TaskRunnerPromptTests.cs](backend.Tests/TaskRunnerPromptTests.cs) (e.g. "user task header appears before run-context header") are necessary, not sufficient.

If the prompt change cannot be live-probed in the current session (no quota, no machine access), say so explicitly; do not silently ship and hope.

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

After any CLI-executed task finishes, check whether [README.md](README.md), [ROADMAP.md](ROADMAP.md), AGENTS.md, [docs/architecture-decisions.md](docs/architecture-decisions.md), or other docs need to be updated. Update them in the same task when the change affects product direction, public behavior, architecture, CLI contracts, filesystem contracts, agent workflow, or established a non-goal / reasoning style worth archiving. See "Architecture Decisions Archive" above for the required shape. If no documentation update is needed, say so briefly in the task report.

When onboarding or resyncing a watched target project, use [.github/prompts/sync-target-instructions.prompt.md](.github/prompts/sync-target-instructions.prompt.md). Target projects should receive an `AGENTS.md` with the agent task contract. Add a lightweight `.github/copilot-instructions.md` only as a compatibility shim when that project still needs Copilot Chat repository instructions.
