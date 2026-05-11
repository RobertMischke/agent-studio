# AGENTS.md

> **Single source of truth for agent instructions.** Read natively by Codex CLI, Claude Code, and the GitHub Copilot coding agent. The files [.github/copilot-instructions.md](.github/copilot-instructions.md) and [CLAUDE.md](CLAUDE.md) are 3-line compatibility shims that point here. Frontend-scoped rules live in [frontend/AGENTS.md](frontend/AGENTS.md) and apply only to changes under `frontend/`.

## Project Overview

Agent Software Studio is a local AI work monitor: a .NET 10 backend plus an Angular 21 frontend that watches external job folders and displays agent progress as a Kanban board.

For product context, read [README.md](README.md) and [ROADMAP.md](ROADMAP.md). The README explains what the tool is and how it is wired. The roadmap explains the product thesis, near-term themes, hard boundaries, and decision principles.

## Documentation lookup

[docs/README.md](docs/README.md) is the hierarchical index of every load-bearing document in this repository, with a one-line description per file. **When you need a doc and don't already know which one, start there** rather than scanning the tree blind. Categories: top-level entry points, architecture / decisions / contracts, CLI integration (per-CLI deep refs), process surfaces, mockups, research, schemas.

When you add a new document under `docs/`, add a one-line row to the index in the same commit so it stays a single grep target. Mockup README files and research notes nest under their existing parent rows; they do not each get their own line.

## Edit only the dev checkout

This repository is checked out twice in the parent `agent-taskboard-devspace/` folder: `agent-taskboard-dev/` (active development) and `agent-taskboard-stable/` (reference). **All edits go to the dev checkout.** Stable receives changes via `git pull` from `main` and is never edited directly.

The dev checkout marks itself visually (orange "DEV" stripe, orange PWA icon, `(DEV)` window title) when `backend/appsettings.Local.json` contains `{ "Environment": { "IsDev": true } }`. That file is gitignored, so the same source tree renders un-marked on stable.

Keep the product boundary clear:
- This repository contains the task processor app source code, prompts, and docs.
- Job folders live in watched target projects under `.orchestrator/jobs/`.
- The app observes external jobs; it should not store runtime job artifacts in this repository.

### Dev backend lifecycle: Playwright-only

Dev's backend (port 5030) is **offline by default**. The only path that may
bring it up is a Playwright spec running from stable that uses the
`dev-backend` fixture in [frontend/e2e/fixtures/dev-backend.ts](frontend/e2e/fixtures/dev-backend.ts).
That fixture calls [scripts/supervisor/dev-lifecycle.sh](scripts/supervisor/dev-lifecycle.sh)
(`start` / `stop` / `status`) and is idempotent: if dev was already up when
the spec loaded, the fixture leaves it running on teardown. Set
`KEEP_DEV_ON_FAIL=1` to keep dev up after a failure for inspection.

Do **not** start dev's backend from a supervisor session, an auto-mode loop,
or any background watcher. The parent `start-dev.sh` / `stop-dev.sh` scripts
in `agent-taskboard-devspace/` remain available for direct human invocation
when debugging dev itself; agents should not call them as part of routine
runs.

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
- Agent Software Studio is the central home for standard skills and project-specific skills.
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
- **Dev stays offline outside Playwright.** Stable is the supervisor seat; dev is the regression-test target. Only Playwright specs running from stable may bring dev's backend up, via the `dev-backend` fixture. Don't add supervisor or auto-mode code paths that start dev as a side effect.
- **Any external resume must verify mode after the backend restart and retry on mismatch.** `update-stable.sh` stops and restarts stable; a resume `PUT /api/runner/<project>/mode` that fires before the new backend is ready, or that is missing the `X-Client-Id` mutation header, silently leaves the project paused. Shell out to `scripts/supervisor/resume-runner.sh` (or replicate its four steps: wait for `/healthz`, auto-register a `service` identity if needed, PUT with `X-Client-Id`, read back `/api/runner/status` and retry the PUT until the project's mode is `auto-continuous`). The in-process equivalent lives in `MetaCycleHostedService.ResumeWithVerificationAsync` and emits a high-severity `cycle-resume-failed` advisory + `[supervisor]` chat-note on persistent failure rather than leaving the operator silently stuck.

When NOT to update stable:

- Dev work is unfinished or untested. Wait for the batch to settle.
- A change is purely exploratory (mockups under `docs/mockups/`, research under `docs/research/`). These do not affect runtime; they can sit in dev.
- The stable instance is currently being used to drive a long task. Wait for the task to finish or stop, then update.

Direct-agent work in this repository should not remain local after it is finished. When Codex or another directly-invoked agent changes source, docs, mockups, prompts, or task evidence, commit the coherent batch and push it before reporting done, unless the user explicitly says not to push. Keep the commit scoped to the files touched for the request and do not sweep in unrelated dirty work.

This rule does not allow worker CLIs spawned by Agent Software Studio to commit or push on their own. Managed task runs still follow [docs/commit-push-doctrine.md](docs/commit-push-doctrine.md): the platform owns the commit and push boundary. The direct-agent rule is for interactive repository work like this file, documentation updates, mockups, and task-queue maintenance.

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

### Experiments kept for reference, not active focus

- **Companion App** (`backend/Services/Companion/`, `frontend/src/app/...`, `docs/companion-app-design.md`, ADR-0018). V1 was built as a relay-based phone PWA so the user could read the board from outside the LAN. It is no longer the active direction. Do not extend it, do not list it as a current capability in the README, and do not propose V2 work. Code stays in the tree as a reference for the relay-based pull-only approach in case a similar problem comes up later.

## Roadmap construction conventions

The roadmap is **future-only** (see [README.md](README.md) for what already exists). Editorial guidance for adding or revising roadmap themes:

- One section per coherent theme. Mix sentence-paragraphs and short lists; avoid wall-of-bullet sections.
- Every theme that has queued work calls out the queue paths under `agent-taskboard/2-ready/...` so the trail from roadmap to actual queued task is visible.
- Sibling themes cross-reference each other ("This is sibling to X" / "Builds on Y") so the dependency graph is readable.
- Hard rules go in their own subsection inside the theme. They are the constraints a future implementer must respect.
- "First implementation order" lists stay numbered and small (5-10 items max). Longer sequences belong in a research doc under `docs/research/` referenced from the theme.

Existing tooling for roadmap work:
- [`prompts/runtime/roadmap-intake.md`](prompts/runtime/roadmap-intake.md) is the splitter that turns a free-text dump into candidate tasks (the Roadmap Intake feature in the orchestrator side sheet uses this).
- [`prompts/runtime/roadmap-alignment-review.md`](prompts/runtime/roadmap-alignment-review.md) is the meta-action that reads the queue + the roadmap and reports drift between them.
- The Test Run Service entry in the roadmap was added today; use it as a shape reference for new themes (intent statement, what it enables, hard rules, first implementation order).

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
4. **Layer 2.5** - the orchestrator meta-cycle. It runs at quiet batch boundaries, not during a CLI run: pause after N jobs reach review, inspect a bounded evidence envelope, write a structured `MetaCycleReport`, then resume, update stable, queue a fix task, or escalate. It reuses Layer 2 pause/resume primitives, never edits source code, and never moves job lanes directly. The spec is [`docs/mockups/orchestrator-meta-cycle/`](docs/mockups/orchestrator-meta-cycle/) and the decision is [ADR-0022](docs/architecture-decisions.md).
5. **Layer 3** - the external system review monitor (`scripts/supervisor/run-system-review.sh`). Stand-alone Claude session driven from outside the app, reads stable's state on a 4-8h cadence, writes a structured Markdown review under the workspace's `logs/system-review/`. Survives any failure mode of the app itself, including the Layer 2 supervisor.

Hard rules:
- The supervisor is **advice-first, force-rare**. Routine outcomes still flow through `RunOutcomePolicy`; the supervisor adds a kill-switch and a soft-reasoning second opinion, not a parallel orchestrator.
- **Single-writer state machine**: emergency primitives never poke job state directly; they call `TaskRunnerService.StopJob` / `SetMode` so there is exactly one cancel implementation.
- **Feedback-loop guard**: every event carries a `Source`. Auto-intervention and observation parsing never act on `Source: AutoIntervention` or `Source: User` events; they would feed back into the loop they came from.
- **Auto-intervention stays gated**: enabling it is a per-instance decision; the rate limit (`Supervisor:AutoInterventionRateLimit`, default 3/h/project) and severity threshold (`Supervisor:AutoInterventionSeverityThreshold`, default High) protect against runaway invocations.
- **Analysis reports are evidence**: manual, scheduled, and meta-cycle analyses write Markdown plus structured JSON when parseable. They may queue follow-up tasks, but they do not silently change source or bypass review lanes.

### Contract-bounded agents and loop guards (ADR-0032)

When an LLM is invoked to interpret evidence on behalf of the orchestrator (failure analysis, drift classification, evidence summarization), the call sits between a typed input contract and a typed output contract. **The agent classifies; the rule engine decides.** Safety- and cost-relevant choices (halt the pipeline, requeue, escalate-human, run a self-heal command) live in deterministic code that maps `(category, confidence)` from the output contract through a fixed action table. They never live in the agent's `proposedAction` directly. The full pattern with diagram, schemas, and worked example is in [docs/agent-contract-pattern.md](docs/agent-contract-pattern.md).

The pattern has three slots, each owned explicitly:

1. **Pre-Guard (rule engine)** before the agent runs: budget checks (attempts/job, attempts/run-set, token spend, wall-clock, age) refuse the call when over limit. Cost circuit breaker; nothing reaches the LLM.
2. **Agent (LLM)** receives a schema-validated `<step>-input.json`, produces a schema-validated `<step>-output.json`. May propose actions; may not execute them.
3. **Decider + Post-Guard (rule engine)** maps the output contract to one of a fixed set of actions via a code table. Post-Guard refuses requeue when the same slug + same category has cycled more than N times; escalates to a human review banner instead.

Hard rules:

- **The agent never decides whether to halt the pipeline.** Halt is a deterministic mapping inside the decider, not the agent's recommendation.
- **`selfHealCommands` are an allow-list, not free shell.** Each entry must match a registered command id in `backend/Services/SelfHeal/SelfHealCommandRegistry.cs`; arbitrary strings are rejected before dispatch.
- **Schema-invalid output = escalate-human.** A malformed output contract is treated as the agent failing closed, never as silent retry.
- **Every contract roundtrip writes both files.** `<run-folder>/contracts/<step>-input.json` and `<step>-output.json`; the rule engine reads back from disk so the boundary is observable, replayable, diffable.
- **Every loop class is registered.** When you add a new place where work can re-enter itself (retry, requeue, re-trigger, replay), add an entry to [docs/loop-inventory.md](docs/loop-inventory.md), a budget constant in code, and a breaker test in `backend.Tests/Architecture/` in the same commit. CI fails if the trio is incomplete.

First worked example: the `3a-failed-pickup` dead-letter (ADR-0028) gets a diagnosis step. On dead-letter, the runner writes `pickup-failure-context.json`; a diagnostic agent returns `pickup-failure-diagnosis.json`. Categories `infra-cli-broken` and `infra-network` always halt the runner and raise a banner regardless of agent confidence; `task-bad-prompt` may requeue once at confidence ≥ 0.8; everything else escalates. See [docs/agent-contract-pattern.md](docs/agent-contract-pattern.md) "Worked example: pickup-failed".

### Service & data layout (backend)

- `Services/Cli/`: one driver per CLI: `ClaudeCliService`, `CodexCliService`, `CopilotCliService`, `GeminiCliService`, all extending `CliExecutionServiceBase` (except Copilot, which predates the base class). `CliRouter` picks the right one by `cliType`. The contract every driver must satisfy is documented in [docs/supported-clis.md](docs/supported-clis.md). **When you touch any of these files, also read the matching skill in [docs/cli-skills/](docs/cli-skills/) - [cli-overview](docs/cli-skills/cli-overview.md) plus the per-CLI skill ([cli-claude](docs/cli-skills/cli-claude.md), [cli-codex](docs/cli-skills/cli-codex.md), [cli-copilot](docs/cli-skills/cli-copilot.md), [cli-gemini](docs/cli-skills/cli-gemini.md)). The skills hold the operational knowledge that doesn't fit in code comments - frame catalogues, capture flows, known incidents, common-task playbooks. This is a hard rule for every CLI driving this repo (Claude Code, Codex, Copilot, Gemini): if the task touches a CLI driver, the matching skill is required reading before any code change. The pickup is enforced by two tests - a free scaffolding lock in [`backend.Tests/CliSkillFilesTests.cs`](backend.Tests/CliSkillFilesTests.cs) and a `@billable` live test in [`frontend/e2e/cli-skills-pickup.spec.ts`](frontend/e2e/cli-skills-pickup.spec.ts) that drives each CLI through the task processor and asserts it can echo back the sentinel string from the matching skill.**
- `Services/TaskAccess/`: the single owner of on-disk job state (ADR-0024). `ITaskAccess` is the read / list / mutate / transition / subscribe surface; `ITaskAccessHost` owns boot / reload / shutdown; `TaskAccessRecords` carries the typed requests, results, optimistic-concurrency token, and change notifications. **Phase 1 ships the contract only**; the in-memory store, mutations, and consumer migration land in phases 2 through 5 of the queued task `task-access-api-layer-extraction`. Once phase 4 ships, no service, hosted service, endpoint, or test outside this folder may read or write job folders directly; every consumer goes through `ITaskAccess`.
- `Services/Jobs/JobTransitionService.cs`: the only path that combines a folder move with its side effects (auto-commit stamping, runner-active-state reconciliation). State mutations on the active job clear `ProjectRunner._activeJobId` atomically: a successful move out of `3-progress` raises `JobTransitionService.OnJobMoved`, the `Program.cs` subscriber calls `TaskRunnerService.ClearActiveJobForProject`, and the runner releases the in-memory latch before any further tick observes it. The defensive sibling on the watcher path (`JobWatcherService.OnJobChanged` + `TaskRunnerService.ReconcileAllRunners`) and on the periodic tick (`ProjectRunner.ReconcileActiveJobAgainstDisk` at the head of `TickAsync`) covers external folder moves that never went through the API. Without this, an external move leaves the runner pinned at a slug whose folder has left the lane, every pickup tick short-circuits on `active != null`, and the project wedges until a backend restart.
- **Strict-iteration progress-first pickup** (ADR-0028). The per-project pickup loop walks **every** `3-progress` folder oldest-first by mtime before considering `2-ready`. A folder qualifies for resume regardless of session state or whether `cli-output.log` exists - the "no log" case means the previous attempt died before the CLI streamed anything, the most-restartable case. Folders whose autopickup runs have produced no CLI output for `PickupFailureThreshold` (default 3) consecutive attempts are dead-lettered into `3a-failed-pickup/<slug>-pickup-failed-<utc-date>/` via `JobStateMachine.MoveFolderToFailedPickup`; one row per dead-letter is appended to `<workspace>/logs/pickup-failures.jsonl` (schema: [docs/schemas/pickup-failure.schema.json](docs/schemas/pickup-failure.schema.json)). Iteration is exhaustive within a tick (every over-budget folder is dead-lettered before the picker stops at the first remaining folder), and only an empty `3-progress` lane lets the runner consider `2-ready`. See `ProjectRunner.TryPickProgressJobOrDeadLetter` and `PickupFailureLog`.
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

States (ADR-0025: three-stage review pipeline):

```text
1-preparation -> 2-ready -> 3-progress -> 4-auto-review -> 5-human-review -> 6-completed -> 7-archive
```

`4-auto-review` is the orchestrator's lane (machine icon in the kanban): the `ReviewDecisionOrchestrator` decides reissue / accept-as-done / escalate. `5-human-review` is the lane that waits for the user (eye icon). The user always gets the final say on completion - the orchestrator never moves a job directly from `4-auto-review` to `6-completed`.

Only jobs in `2-ready` or `3-progress` can be started via `/api/jobs/{id}/start`. New jobs default to `1-preparation`; the create endpoint accepts an optional `targetState` to land directly in `2-ready`.

Successful CLI runs move from `3-progress` to `4-auto-review` through application code. Failed or stopped runs stay in `3-progress` for inspection, restart, or continuation. The pre-ADR-0025 single `4-review` lane is migrated automatically on backend boot via `JobStateMachine.EnsureStateFoldersAndMigrate`.

### Job organization rule: API first

Agents must organize jobs through the application API, not by directly creating, moving, deleting, or reordering folders in `agent-taskboard-workspace/projects/<projectKey>/`. This applies to **every agent surface**: the orchestrator-managed CLI runs, direct-from-VS-Code Codex / Claude Code / Copilot / Gemini sessions, and any ad-hoc shell session a human or LLM drives.

Use:

- `GET /api/watch-paths` to find the effective `watchPath`.
- `POST /api/jobs` with `CreateJobRequest` to create jobs.
- `POST /api/jobs/{jobId}/move?watchPath=...` to move jobs.
- `POST /api/jobs/reorder` to reorder jobs.
- `POST /api/jobs/{jobId}/move-to-top?watchPath=...` to promote a queued job.
- `POST /api/jobs/{jobId}/change-project?watchPath=...` to relocate a job between watched workspaces.
- `DELETE /api/jobs/{jobId}?watchPath=...` to delete jobs.
- `PUT /api/jobs/{jobId}/state?watchPath=...` plus the other `PUT /api/jobs/{jobId}/*` field-edit endpoints for content changes.

**Forbidden, even as a one-shot convenience:** `mv`, `rm`, `cp`, `mkdir`, `Move-Item`, `Remove-Item`, `Rename-Item`, or any other shell / filesystem command against a slug folder under `agent-taskboard-workspace/projects/<projectKey>/<lane>/`. Editing `state` inside a `job.json` by hand to "fix" a lane mismatch is the same bypass and is also forbidden. Filesystem state and the in-memory index diverge silently when these run, which is exactly what produced the 2026-05-09 zombie folder + 409 conflict. The architecture test [`backend.Tests/Architecture/JobFolderAccessIsolationTest.cs`](backend.Tests/Architecture/JobFolderAccessIsolationTest.cs) catches code-side bypasses; the LLM behavioural side is this rule.

If you need an operation the API does not expose (currently: batch restore / batch move; archived-folder content reads after archive sweep), surface the gap as a queued task rather than reaching past the API. Bulk operations are explicitly an open follow-up - see the API completeness audit at the top of [`task-access-api-layer-extraction`](docs/architecture-decisions.md) (ADR-0024) and queue a new task if you hit a missing surface.

Direct filesystem changes by application code itself are bounded by the same architecture test: only `backend/Services/Jobs/*`, `backend/Services/JobWatcherService.cs`, `backend/Services/Runner/CrashRecoveryService.cs`, and the `backend/Services/TaskAccess/` layer (today only the contract; phases 2-4 land the implementation) may construct lane folder paths or call `Directory.Move` / `Directory.Delete`. Everything else - endpoints, hosted services, analysis services - goes through the typed API. Backend migrations, recovery code paths, and tests that intentionally exercise the filesystem contract live behind that boundary; new direct-access call sites trip the architecture test on the way in.

See `docs/filesystem-contract.md` for full details.

## Code Conventions

- Frontend uses Angular signals for state.
- Frontend components are standalone; do not introduce NgModules.
- Keep the existing dark Catppuccin-inspired UI direction.
- Keep the detail view as a simple protocol view, without tabs or metrics grids unless the product direction changes.
- Prefer small, scoped changes and avoid rewriting unrelated code.

### Selectors in Playwright tests

Prefer `data-testid="..."` for stable test hooks. If a feature you're touching lacks one and a spec needs it, add it to the component rather than reaching for a CSS-class selector.

### Mockups must be interactive

Mockups created in this repository should be meaningfully interactive, even when they are short-lived design artifacts. Static screenshots are supporting evidence, not the mockup itself.

For HTML mockups:

- Core controls should be clickable: mode switches, menus, drawers, overlays, expanders, filters, settings, and start/stop-style controls.
- Important states should be reachable in the mockup without editing the file.
- Technical details should be expandable when the concept depends on drill-down.
- If a concept includes overlays or context menus, implement those overlays.
- Render and inspect Playwright screenshots for the important states before declaring the mockup ready.

If a mockup deliberately stays static, document why in the mockup README.

## Watched Target Instructions

This repository uses an app-owned task lifecycle for dependent projects. The shared boundary is defined in [docs/agent-task-contract.md](docs/agent-task-contract.md); treat it as the single source of truth for what the application controls and what the CLI agent controls.

After any CLI-executed task finishes, check whether [README.md](README.md), [ROADMAP.md](ROADMAP.md), AGENTS.md, [docs/architecture-decisions.md](docs/architecture-decisions.md), or other docs need to be updated. Update them in the same task when the change affects product direction, public behavior, architecture, CLI contracts, filesystem contracts, agent workflow, or established a non-goal / reasoning style worth archiving. See "Architecture Decisions Archive" above for the required shape. If no documentation update is needed, say so briefly in the task report.

When onboarding or resyncing a watched target project, use [.github/prompts/sync-target-instructions.prompt.md](.github/prompts/sync-target-instructions.prompt.md). Target projects should receive an `AGENTS.md` with the agent task contract. Add a lightweight `.github/copilot-instructions.md` only as a compatibility shim when that project still needs Copilot Chat repository instructions.
