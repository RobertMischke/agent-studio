# AGENTS.md

> **Single source of truth for agent instructions.** Read natively by Codex CLI, Claude Code, and the GitHub Copilot coding agent. The files [.github/copilot-instructions.md](.github/copilot-instructions.md) and [CLAUDE.md](CLAUDE.md) are 3-line compatibility shims that point here. Frontend-scoped rules live in [frontend/AGENTS.md](frontend/AGENTS.md) and apply only to changes under `frontend/`.

## Project Overview

agent-orchestrator is a local AI work monitor: a .NET 10 backend plus an Angular 21 frontend that watches external task folders and displays agent progress as a Kanban board.

For product context, read [README.md](README.md) and [ROADMAP.md](ROADMAP.md). The README explains what the tool is and how it is wired. The roadmap explains the product thesis, near-term themes, hard boundaries, and decision principles.

## Documentation lookup

[docs/README.md](docs/README.md) is the hierarchical index of every load-bearing document in this repository, with a one-line description per file. **When you need a doc and don't already know which one, start there** rather than scanning the tree blind. Categories: top-level entry points, architecture / decisions / contracts, CLI integration (per-CLI deep refs), process surfaces, mockups, research, schemas.

For operator-side setup (attaching a new project, onboarding a new CLI agent, first task walkthrough, troubleshooting) see [docs/setup/](docs/setup/README.md). It's the user-facing companion to the install quickstart in [docs/getting-started.md](docs/getting-started.md).

When you add a new document under `docs/`, add a one-line row to the index in the same commit so it stays a single grep target. Mockup README files and research notes nest under their existing parent rows; they do not each get their own line.

## Common Problems Wiki

Before debugging a familiar-looking runtime, CLI, permission, filesystem, runner, or state-machine failure, search [docs/wiki/common-problems/](docs/wiki/common-problems/). The project wiki is the durable memory for recurring operational problems: symptoms, occurrences, root-cause protocol, measures, ideas, and related tasks.

Use [docs/wiki/README.md](docs/wiki/README.md) for the conventions. New entries go through `scripts/wiki/new-problem.sh <slug>`, must pass `scripts/wiki/lint.sh`, and the generated index [docs/wiki/common-problems/README.md](docs/wiki/common-problems/README.md) must be rebuilt with `scripts/wiki/regenerate-index.sh`. Do not edit the generated index by hand.

## STOP — read this before any file action under `agent-taskboard-workspace/`

**Never create, move, edit, delete, or rename folders or files under `agent-taskboard-workspace/projects/**` or `agent-taskboard-workspace/.metadata/**` with `Write`, `Edit`, `mv`, `rm`, `cp`, `mkdir`, PowerShell `Move-Item` / `Remove-Item` / `New-Item`, or any other direct filesystem command — not even "just this once" to file a new job, fix a slug, or move a card.** The application API is the only allowed mutation path. The full enumeration (create job, move, batch-move, restore-from-failed-pickup, delete, edit fields) and the rationale (filesystem state and in-memory index diverge silently → zombie folders, 409 conflicts, orphaned runs) live in [Job organization rule: API first](#job-organization-rule-api-first) further down. This callout exists because the rule keeps getting violated. The architecture test [`backend.Tests/Architecture/JobFolderAccessIsolationTest.cs`](backend.Tests/Architecture/JobFolderAccessIsolationTest.cs) enforces the code-side; this rule is the agent-side and applies to **every** agent (Claude Code, Codex, Copilot, Gemini, any future surface).

If the API does not expose an operation you need, **queue a new task** rather than reaching past it.

## Edit only the dev checkout

This repository is checked out twice in the parent `agent-taskboard-devspace/` folder: `agent-taskboard-dev/` (active development) and `agent-taskboard-stable/` (reference). **All edits go to the dev checkout.** Stable receives changes via `git pull` from `main` and is never edited directly.

The dev checkout marks itself visually (orange "DEV" stripe, orange PWA icon, `(DEV)` window title) when `backend/appsettings.Local.json` contains `{ "Environment": { "IsDev": true } }`. That file is gitignored, so the same source tree renders un-marked on stable.

Keep the product boundary clear:
- This repository contains the task processor app source code, prompts, and docs.
- Task folders live in watched target projects under `.orchestrator/jobs/`.
- The app observes external tasks; it should not store runtime task artifacts in this repository.

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

**ADR-0044 enforcement.** The rule used to live in this document only; on
2026-05-28 it was made structural. The dev backend now boots with
`Runner:Role=test-subject` (set in `backend/appsettings.Local.json`); the
per-project pickup tick checks the role and returns early before considering
the queue, so a `test-subject` backend cannot auto-pick even when its mode
is left at `auto-continuous`. A disk-backed `.pickup-lock.json` on the job
folder (pid + hostname + role + backend name) is the cross-process
belt-and-braces: a foreign live owner causes the second backend's
`RunCliAsync` to refuse the spawn with `ProjectBusy`. The parent
`start-dev.sh` also gates on `ATP_ALLOW_DEV_BACKEND=1` so a human boot is
an explicit acknowledgement of the policy; `dev-lifecycle.sh` exports
`ATP_DEV_BACKEND_FROM_FIXTURE=1` to bypass the gate because the Playwright
fixture is the one legitimate caller. Operator-initiated `manual` /
`paused` mode changes that arrive while a job is active are now *deferred*
(the live mode stays at its `auto-*` value, the requested mode lands in
`PendingMode`, and the runner applies it on the next active-job clear);
the `PUT /api/runner/{project}/mode` response carries
`{applied, mode, pendingMode, willApplyAfterJobId}` so the lane pill can
render "MANUAL (after current)" without polling status a second time.
Full rationale + non-goals in [docs/architecture-decisions.md](docs/architecture-decisions.md) under ADR-0044.

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
- Skills are optional, situational workflow guides. They explain how to do a specialist task; they must not own task lifecycle, state movement, review transitions, or queue policy.
- agent-orchestrator is the central home for standard skills and project-specific skills.
- Watched child projects should expose a small README or agent-instruction lookup section that tells direct CLI agents where to find the relevant skills.

This lookup section matters because users may work both through the orchestrator and directly in Codex, Claude Code, Copilot, or Gemini from VS Code. Managed taskboard runs can attach selected skills explicitly; direct CLI sessions rely on the watched project's README or AGENTS.md lookup section. Native CLI skill exports may come later, but the Markdown lookup is the shared baseline.

### Central skill library

The active skill set lives under [`.agents/skills/`](.agents/skills/README.md). Today:

| Skill | Read when |
|-------|-----------|
| [**Task API**](.agents/skills/job-api/SKILL.md) | The task includes creating, moving, reissuing, archiving, or bulk-triaging tasks via HTTP. Required reading before any scripted board mutation; covers the `watchPath`-quirk, `X-Client-Id` header, lane vocabulary, and ready-to-use Node templates for create / move-state / move-to-top / triage. Examples of "this applies": "lege einen Bug-Task an", "schick die Open-Items zurück nach Ready", "räum die Failed-Pickup-Lane auf". |
| [Regenerate README](.agents/skills/regenerate-readme/SKILL.md) | A load-bearing change requires a README rewrite. |
| [Runtime log analysis](.agents/skills/runtime-log-analysis/SKILL.md) | Inspecting backend / runner / CLI logs after an incident. |

The library is for **every** CLI driving this repo - Claude Code, Codex, Copilot, Gemini. If you write a one-off shell snippet to move a task, you missed the Task API skill; copy the Node template from [`scripts/`](.agents/skills/job-api/scripts) instead.

When changing skill behavior, keep [docs/architecture-decisions.md](docs/architecture-decisions.md) in sync if the change affects this boundary.

## Stable update policy

The dev checkout (`agent-taskboard-dev/`) and the stable checkout (`agent-taskboard-stable/`) sit side by side under `agent-taskboard-devspace/`. All edits go to dev. Stable receives changes via `update-stable.sh` in the parent folder, which `git pull --ff-only`s from `origin/main`, runs `npm install` if `package-lock.json` changed, and restarts stable. **Stable is never edited directly.**

When to update stable:

- **After a coherent batch ships and is verified in dev.** A "batch" is a feature plus its tests plus its documentation, all green. Single-commit speculative pushes to stable add risk for no gain.
- **Before a long unattended run.** If you are about to leave the orchestrator working on a board for hours, update stable first so the running version matches the documented behaviour. The Layer 3 system review monitor watches stable; running it against an out-of-date stable wastes the run.
- **After a load-bearing change to runner / supervisor / outcome-policy / agent contract.** These are observation surfaces; if dev and stable diverge on them, the activity log and supervisor logs disagree on what the system is actually doing.
- **Never mid-run.** `update-stable.sh` stops stable. If a task is in `3-progress` on stable, finish or stop it explicitly first.
- **Dev stays offline outside Playwright.** Stable is the supervisor seat; dev is the regression-test target. Only Playwright specs running from stable may bring dev's backend up, via the `dev-backend` fixture. Don't add supervisor or auto-mode code paths that start dev as a side effect.
- **Any external resume must verify mode after the backend restart and retry on mismatch.** `update-stable.sh` stops and restarts stable; a resume `PUT /api/runner/<project>/mode` that fires before the new backend is ready, or that is missing the `X-Client-Id` mutation header, silently leaves the project paused. Shell out to `scripts/supervisor/resume-runner.sh` (or replicate its four steps: wait for `/healthz`, auto-register a `service` identity if needed, PUT with `X-Client-Id`, read back `/api/runner/status` and retry the PUT until the project's mode is `auto-continuous`). The in-process equivalent lives in `MetaCycleHostedService.ResumeWithVerificationAsync` and emits a high-severity `cycle-resume-failed` advisory + `[supervisor]` chat-note on persistent failure rather than leaving the operator silently stuck.

When NOT to update stable:

- Dev work is unfinished or untested. Wait for the batch to settle.
- A change is purely exploratory (mockups under `docs/mockups/`, research under `docs/research/`). These do not affect runtime; they can sit in dev.
- The stable instance is currently being used to drive a long task. Wait for the task to finish or stop, then update.

Direct-agent work in this repository should not remain local after it is finished. When Codex or another directly-invoked agent changes source, docs, mockups, prompts, or task evidence, commit the coherent batch and push it before reporting done, unless the user explicitly says not to push. Keep the commit scoped to the files touched for the request and do not sweep in unrelated dirty work.

This rule does not allow worker CLIs spawned by agent-orchestrator to commit or push on their own. Managed task runs still follow [docs/commit-push-doctrine.md](docs/commit-push-doctrine.md): the platform owns the commit and push boundary. The direct-agent rule is for interactive repository work like this file, documentation updates, mockups, and task-queue maintenance.

Layer 3 - the system review monitor at [`scripts/supervisor/run-system-review.sh`](scripts/supervisor/run-system-review.sh) - reads stable's state read-only. It is the most useful right after a stable update has happened: it can confirm the new code is running, capture a baseline, and surface any drift against the just-shipped behaviour.

## Documentation Language

All written artifacts in this repository (README, ROADMAP.md, AGENTS.md, docs/, prompts, code comments, commit messages, PR descriptions) are written in **English**. Chat conversation with the user may happen in any language, but anything you commit or write to disk in this repo stays English.

Generated repository text must not use em dashes. Prefer a normal hyphen, a comma, a semicolon, or a new sentence.

User-facing application strings (UI labels, button text, banner copy, backend error messages surfaced to the UI) are **English** as well, even when the user dictates them in another language. Existing German strings are legacy and may be migrated opportunistically when you're already touching the surrounding code, but never introduce new non-English strings.

## Product Goal & Non-Goals

The task processor drives an automated pipeline of tasks per project. It is **sequential by default** (`maxParallelism = 1`); **bounded intra-project parallelism is an opt-in, orchestrator-gated capability** (ADR-0052), not a hard non-goal. Parallelism also exists across projects.

In scope:
- Sequential, automated task execution **within a single project**, by default. Tasks queued on that project's board are picked up and processed automatically, without per-task human kick-off.
- **Parallelism across projects**. Different watched projects (different watch paths) run their own pipelines independently and may execute concurrently.
- **Opt-in intra-project parallelism (ADR-0052).** A per-project `maxParallelism` runs N tasks of one project concurrently, each isolated in its own **git worktree** on a short-lived `task/<id>` branch off the configurable integration branch (default `develop`). The **orchestrator decides** parallelisability (a too-big / cross-cutting task is flagged `exclusive` and runs alone); all git handling (worktree, commit, merge/PR) lives in pre/post pipeline steps, never in the run agent.
- Minimum overhead. The default stays single-task; the bookkeeping only exists when a project opts into parallelism.

Out of scope (do not add, even if asked offhandedly):
- **Workspaces / workflows.** No multi-step workflow engine, no per-task workspace creation.
- **Unbounded / ungated parallelism.** Parallelism is always capped by `maxParallelism` and gated by the orchestrator's parallelisability decision; no fan-out that runs conflicting tasks concurrently or lets the run agent manage git itself.

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
- [`prompts/runtime/roadmap-alignment-review.md`](prompts/runtime/roadmap-alignment-review.md) is the meta-action that reads the queue + the roadmap and reports drift between them.
- The Test Run Service entry in the roadmap was added today; use it as a shape reference for new themes (intent statement, what it enables, hard rules, first implementation order).

## Architecture

| Layer | Path | Notes |
|-------|------|-------|
| Backend API | `backend/` | ASP.NET Core, runs on `http://localhost:5030`, SignalR hub for live push. |
| Backend tests | `backend.Tests/` | xUnit. |
| Frontend | `frontend/` | Angular 21 standalone components, signals state, PWA, runs on `http://localhost:4010`. |
| E2E tests | `frontend/e2e/` | Playwright. See [frontend/e2e/README.md](frontend/e2e/README.md). |
| Filesystem contract | `docs/filesystem-contract.md` | Task folder layout. |
| Design principles | `docs/design-principles.md` | UX contract for the abstraction layer over agents + software: top-level summary, always-available drill-down, run-as-unit-of-conversation. |
| Protocol & image style | `docs/protocol-style.md` | `status.md` shape, Activity Log markers, `attachments/` vs `results/`, per-CLI image retention. |
| Agent task contract | `docs/agent-task-contract.md` | App-owned lifecycle boundary copied into watched targets. |
| Orchestrator chat | `docs/orchestrator-chat.md` | Persistent global and project orchestrator chat, visible memory, scope, forks, and typed app actions. |
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
3. `OrchestratorChatLog` (in [backend/Services/Runner/OrchestratorChatLog.cs](backend/Services/Runner/OrchestratorChatLog.cs)) writes typed orchestrator messages (`decision`, `reissue`, `heuristic`, `steer`, `giveup`, plus operational kinds like `intervention`, `permission-blocked`, `watchdog`, `environment-blocker`) into `logs/cli-output.log` on the `[orchestrator]` stream. The frontend's activity-log parser renders them as a separate participant alongside `You` and the agent.

The orchestrator's own reply grammar is `{REPLY | STEER | BLOCK}` (parsed by [backend/Services/Runner/OrchestratorReplyParser.cs](backend/Services/Runner/OrchestratorReplyParser.cs)). `REPLY` becomes a user-style follow-up the runner re-issues to the agent. `STEER` is the productive escalation: when the orchestrator cannot pick a path on its own but can name a concrete unblocker, it returns a `STEER` block with `Need:` / `Why:` / optional `Options:` lines; the runner writes it as a typed `steer` chat message and leaves the job in `NeedsInput` (no re-issue, stuck-loop counter still ticks). `BLOCK` is the last-resort "no productive ask" path. Malformed `STEER` degrades to `BLOCK` so the user is never stranded with an opaque marker. When you change the orchestrator prompt or parser, keep `OrchestratorPrompts_TeachSteer` and the parser tests green - they grammar-lock the load-bearing tokens (`STEER`, `Need:`, `Why:`, `Options:`, `BLOCK`).

When you change a CLI driver, prompt template, or the runner's post-run path, keep these three layers in mind. The agent contract that backs the sentinel grammar lives in [docs/agent-task-contract.md](docs/agent-task-contract.md); when you add or change a sentinel, update both that file and `AgentOutcomeAnalyzer.SentinelRegex`.

### Multi-loop supervision (above the runner)

The runner described above is itself the second loop in a four-layer model. When you change supervisor code or wire a new check, keep the model and the load-bearing rules in mind:

1. **Layer 0** - the CLI agent's own loop (Claude / Codex / Copilot / Gemini). Owned by the vendor. The orchestrator can only start, observe, kill.
2. **Layer 1** - the orchestrator's task-pickup loop per project. The deterministic post-run policy (ADR-0002) decides outcomes here.
3. **Layer 2** - the per-project supervisor (`backend/Services/Supervisor/*`). Watches Layer 1 in real time. Default behaviour is cooperative signalling: writes typed `SupervisorAdvisory` records to `logs/meta/<project>/observations.jsonl`. Four pre-emptive primitives (`cancelRun`, `pausePickup`, `forceFail`, `resume`) exist for the rare emergency and route through the existing runner methods so the runner stays the single state-machine authority. Auto-intervention is a separate opt-in policy (`Supervisor:AutoInterventionEnabled`, default false). The full design rationale is in [`docs/research/orchestrator-meta-loop-analysis-2026-05-04.md`](docs/research/orchestrator-meta-loop-analysis-2026-05-04.md) and [ADR-0017](docs/architecture-decisions.md).
4. **Layer 2.5** - the orchestrator meta-cycle. It runs at quiet batch boundaries, not during a CLI run: pause after N tasks reach review, inspect a bounded evidence envelope, write a structured `MetaCycleReport`, then resume, update stable, queue a fix task, or escalate. It reuses Layer 2 pause/resume primitives, never edits source code, and never moves task lanes directly. The spec is [`docs/mockups/orchestrator-meta-cycle/`](docs/mockups/orchestrator-meta-cycle/) and the decision is [ADR-0022](docs/architecture-decisions.md).
5. **Layer 3** - the external system review monitor (`scripts/supervisor/run-system-review.sh`). Stand-alone Claude session driven from outside the app, reads stable's state on a 4-8h cadence, writes a structured Markdown review under the workspace's `logs/system-review/`. Survives any failure mode of the app itself, including the Layer 2 supervisor.

Hard rules:
- The supervisor is **advice-first, force-rare**. Routine outcomes still flow through `RunOutcomePolicy`; the supervisor adds a kill-switch and a soft-reasoning second opinion, not a parallel orchestrator.
- **Single-writer state machine**: emergency primitives never poke task state directly; they call `TaskRunnerService.StopJob` / `SetMode` so there is exactly one cancel implementation.
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

Second worked example: the **abort-review** step (ADR-0054, default-OFF per-project `PipelineCatalogue.AbortReviewStep`). On a non-clean run end (watchdog timeout, non-zero exit, unexpected stop) the orchestrator writes `contracts/post-abort-review-input.json` (abort reason + phase, cli-output tail, `tool-calls.jsonl` liveness, git state, task goal, transcript usage); `PostAbortReviewStepService` returns `post-abort-review-output.json` with a `[[ABORT_REVIEW: ...]]` verdict (`empfehlung` ∈ `rerun | staerkeres-reissue | human-review | accept`). The pure `PostAbortReviewDecider` owns the call: `rerun` / `staerkeres-reissue` re-issue only while the per-job budget (`abort-review.rerun-per-job`, default 2) lasts and otherwise escalate; a null/unparseable verdict fails closed to human review; `accept` continues without consuming budget. The loop trio (loop-inventory entry, `PostAbortReviewDecider.DefaultRerunBudget`, `AbortReviewRerunBreakerTest`) is in place per the rule above.

**Two distinct orchestrator-review steps (not one verdict shown twice).** The post bracket carries two separate orchestrator steps with distinct ids, DisplayNames, and verdict semantics; only the second is the final verdict:

- `post-orchestrator-review` (`PipelineCatalogue.OrchestratorReviewStepId`, DisplayName `PostCoreReviewDisplayName` = "Post-Core Orchestrator-Review") runs **directly after Core, before the aspect/tool fan-out**. It is an **early completeness gate** (open-items / unfinished-evidence check); `ReviewDecisionOrchestrator.RecordOrchestratorReviewStep` records its own gate verdict (e.g. `complete` / `reissue` / `escalate`). It is **not** a final verdict and must never render "FINAL VERDICT" or "accepted as done".
- `post-orchestrator-decision` (`PipelineCatalogue.OrchestratorDecisionStepId`, DisplayName `FinalOrchestratorReviewDisplayName` = "Final Orchestrator-Review") runs **after the aspects and tools**. It is the **single final verdict** (`accept` / `reissue` / `escalate`), recorded by `RecordOrchestratorDecisionStep`. The operator-facing "Auto-review accepted … Moved to 5-human-review" chat note belongs to this step only.

The frontend Overview pipeline keys the "FINAL VERDICT" badge and the final-verdict divider off the final step id alone (`isFinalVerdict`, not "any orchestrator row"), suppresses the redundant DECISION group header, and the completion-loop strip suppresses its gap/note line on an `accepted` verdict so the accept message is not duplicated above the steps.

### Service & data layout (backend)

- `Services/Cli/`: one driver per CLI: `ClaudeCliService`, `CodexCliService`, `CopilotCliService`, `GeminiCliService`, all extending `CliExecutionServiceBase` (except Copilot, which predates the base class). `CliRouter` picks the right one by `cliType`. The contract every driver must satisfy is documented in [docs/supported-clis.md](docs/supported-clis.md). **When you touch any of these files, also read the matching skill in [docs/cli-skills/](docs/cli-skills/) - [cli-overview](docs/cli-skills/cli-overview.md) plus the per-CLI skill ([cli-claude](docs/cli-skills/cli-claude.md), [cli-codex](docs/cli-skills/cli-codex.md), [cli-copilot](docs/cli-skills/cli-copilot.md), [cli-gemini](docs/cli-skills/cli-gemini.md)). The skills hold the operational knowledge that doesn't fit in code comments - frame catalogues, capture flows, known incidents, common-task playbooks. This is a hard rule for every CLI driving this repo (Claude Code, Codex, Copilot, Gemini): if the task touches a CLI driver, the matching skill is required reading before any code change. The pickup is enforced by two tests - a free scaffolding lock in [`backend.Tests/CliSkillFilesTests.cs`](backend.Tests/CliSkillFilesTests.cs) and a `@billable` live test in [`frontend/e2e/cli-skills-pickup.spec.ts`](frontend/e2e/cli-skills-pickup.spec.ts) that drives each CLI through the task processor and asserts it can echo back the sentinel string from the matching skill.**
- `Services/TaskAccess/`: the single owner of on-disk task state (ADR-0024). `ITaskAccess` is the read / list / mutate / transition / subscribe surface; `ITaskAccessHost` owns boot / reload / shutdown; `TaskAccessRecords` carries the typed requests, results, optimistic-concurrency token, and change notifications. **Phase 1 ships the contract only**; the in-memory store, mutations, and consumer migration land in phases 2 through 5 of the queued task `task-access-api-layer-extraction`. Once phase 4 ships, no service, hosted service, endpoint, or test outside this folder may read or write task folders directly; every consumer goes through `ITaskAccess`.
- `Services/Jobs/JobTransitionService.cs`: the only path that combines a folder move with its side effects (auto-commit stamping, runner-active-state reconciliation). State mutations on the active task clear `ProjectRunner._activeJobId` atomically: a successful move out of `3-progress` raises `JobTransitionService.OnJobMoved`, the `Program.cs` subscriber calls `TaskRunnerService.ClearActiveJobForProject`, and the runner releases the in-memory latch before any further tick observes it. The defensive sibling on the watcher path (`JobWatcherService.OnJobChanged` + `TaskRunnerService.ReconcileAllRunners`) and on the periodic tick (`ProjectRunner.ReconcileActiveJobAgainstDisk` at the head of `TickAsync`) covers external folder moves that never went through the API. Without this, an external move leaves the runner pinned at a slug whose folder has left the lane, every pickup tick short-circuits on `active != null`, and the project wedges until a backend restart.
- **Strict-iteration progress-first pickup** (ADR-0028). The per-project pickup loop walks **every** `3-progress` folder oldest-first by mtime before considering `2-ready`. A folder qualifies for resume regardless of session state or whether `cli-output.log` exists - the "no log" case means the previous attempt died before the CLI streamed anything, the most-restartable case. Folders whose autopickup runs have produced no CLI output for `PickupFailureThreshold` (default 3) consecutive attempts are dead-lettered into `3a-failed-pickup/<slug>-pickup-failed-<utc-date>/` via `JobStateMachine.MoveFolderToFailedPickup`; one row per dead-letter is appended to `<workspace>/logs/pickup-failures.jsonl` (schema: [docs/schemas/pickup-failure.schema.json](docs/schemas/pickup-failure.schema.json)). Iteration is exhaustive within a tick (every over-budget folder is dead-lettered before the picker stops at the first remaining folder), and only an empty `3-progress` lane lets the runner consider `2-ready`. See `ProjectRunner.TryPickProgressJobOrDeadLetter` and `PickupFailureLog`.
- `Services/Cli/SessionRegistry.cs`: discovers sessions on disk and builds the `/api/cli/usage` report.
- `Services/Quota/*QuotaProbe.cs`: per-CLI quota probes. `QuotaService` aggregates and serves `/api/cli/quota` (with background refresh).
- `Services/Pty/`: PTY-based slash-command probes (used for parsing `/usage`, `/status`).
- `Models/`: DTOs: `JobInfo`, `JobDetail`, `CliExecution`, `CreateJobRequest`, `StartJobRequest`, etc.
- `Endpoints/JobEndpoints.cs`: all routes. Read here first when wiring a new feature.

### Key REST endpoints

| Method | Path | Purpose |
|--------|------|---------|
| GET | `/api/jobs` | List all tasks (flat). |
| GET | `/api/jobs/grouped` | Tasks grouped by state. |
| GET | `/api/jobs/{jobId}?watchPath=...` | One `JobDetail` (info + prompt + status + log). |
| POST | `/api/jobs` | Create task (`CreateJobRequest`). |
| POST | `/api/jobs/batch-move` | Move many tasks in one call (per-item atomic; `BatchMoveRequest`). |
| POST | `/api/jobs/{jobId}/restore-from-failed-pickup?watchPath=...` | Lift a folder out of `3a-failed-pickup` back into `2-ready`, dropping the `-pickup-failed-<utc>` suffix; optional body `{"keepDeadLetterSlug": true}` retains it. |
| POST | `/api/jobs/{jobId}/start?watchPath=...` | Start CLI execution. |
| POST | `/api/jobs/{jobId}/stop?watchPath=...` | Cancel running execution. |
| POST | `/api/jobs/{jobId}/continue?watchPath=...` | Resume with new prompt (same session). |
| GET | `/api/jobs/{jobId}/output?watchPath=...` | CLI stdout/stderr buffer. |
| GET | `/api/jobs/{jobId}/runs?watchPath=...` | Per-task run timeline: ordered CLI invocations between user inputs + aggregates (RunCount, FirstStartedAt, LastActivityAt, HasActiveRun). Drives the protocol-pane run cards. |
| GET | `/api/jobs/{jobId}/runs/{index}/commits?watchPath=...` | Git commits whose author date falls in run #index's wall-clock window. Drives the per-run software-side change set. |
| GET | `/api/cli/usage` | Sessions + versions for all CLIs. |
| GET | `/api/cli/quota` | Per-CLI quota windows (used%, reset times). |
| GET | `/api/cli/{cliType}/models` | Model catalog for one CLI. |
| GET | `/api/watch-paths` | Configured workspaces (legacy: returns the effective `WatchPaths` plus any pointer resolution). |
| GET | `/api/workspaces` | Workspace registry with embedded projects (ADR-0042). |
| GET | `/api/projects` | Project registry, flat list. Pass `?includeArchived=true` to include archived rows. |
| GET | `/api/projects/{PROJ-NNN}` | Full project record by canonical id. The route regex is locked to `^PROJ-\d{3,}$` so it cannot collide with the legacy display-name routes. |

`jobId` (URL slug) + `watchPath` (project root) is the addressing scheme today. `jobKey` is `watchPath::jobId` and is used internally only. ADR-0042 introduces a parallel id-based scheme (`PROJ-NNN`); both coexist while consumers are migrated, and resolution flows through [`JobKeyResolver`](backend/Services/Jobs/JobKeyResolver.cs).

### Project + workspace registry (ADR-0042)

Workspaces and projects live as registry records under `<TaskRepository>/.metadata/{workspaces.json,projects.json}`. Each project has an immutable id (`PROJ-001`, `PROJ-002`, …), a display name (editable), a short code (used in task keys like `ATP-130`), a workspace membership, an optional color, sort order, and an archived flag. Workspaces are pure metadata (no folder per workspace) with their own ids, sort order, color, and an `IsDefault` flag.

The registry is populated by a boot-time pass ([`RegistryBootstrap.Run`](backend/Services/Registry/RegistryBootstrap.cs)) that walks the configured `WatchPaths` and inserts a record for any storage location not already known. Once a record exists, the registry is the source of truth for that project: `WatchPaths` entries that disappear from `appsettings.Local.json` do **not** remove their registry records, and a `WatchPaths` rename does not propagate to the registry's `DisplayName`. **Editing the registry on disk is reserved for migrations and tests**; consumers go through the typed `WorkspaceRegistry` / `ProjectRegistry` singletons or, for read access, the `/api/workspaces` and `/api/projects` endpoints.

Today F45a (the read surface + bootstrap) is in place. The write-side mutations (create / rename / archive / reassign workspace / reorder / color edit), the jobKey-format migration, and the frontend tree integration are tracked under follow-up tasks F45b, F45c, and F46. Until they ship, edits to workspace + project metadata happen through `WatchPaths` (display name and storage path only) and any registry record's `DisplayName` is whatever was inferred at first-boot bootstrap.

### Watched workspaces (legacy bootstrap source)

Local watch configuration lives in gitignored `backend/appsettings.Local.json`. Each `WatchPaths` entry seeds a registry record at first boot (see above). `/api/watch-paths` continues to enumerate the effective watch paths at runtime (including pointer resolution through `.orchestrator.yml` and `TaskRepository`) for legacy consumers. Never hardcode paths in tests; read them from there. New code should prefer `/api/workspaces` / `/api/projects` where it has a choice.

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

Endpoints that are polled by the UI carry an extra obligation: the perf test must reflect a realistic workload (≥ 150-200 tasks for the kanban endpoints) so a future O(N²) regression cannot hide behind a small fixture. New per-task overlay logic that calls back into a scanner method is a smell; review it on the way in.

#### When the symptom is in the UI, measure in the UI

The user's seat is the browser. A green API timing does not prove the UI is fast - change detection, computeds, blocking renders, and stacked polls all live above the API and the user feels them as lag the moment a single one regresses. **When the report mentions the UI ("Detail-Ansicht laggt", "Create dauert lang", "scrolling stutters"), the regression test belongs in [`frontend/e2e/`](frontend/e2e/), not in [`backend.Tests/`](backend.Tests/).**

Three Playwright primitives cover most cases, all CLI-friendly (no UI required), all collected as helpers in [`frontend/e2e/helpers/timing.ts`](frontend/e2e/helpers/timing.ts):

- **`apiRoundtrip(page, urlGlob, trigger)`** - times an outbound HTTP call from inside the running app via `page.waitForResponse`. Matches what the app's polling actually pays (HttpClient overhead + interceptors + browser queue), not what `curl` shows. Use for "polled endpoint stays under N ms from the browser's seat".
- **`startLongTaskRecorder(page)`** - installs a `PerformanceObserver` for `longtask` entries (browser definition: any main-thread block > 50 ms). Returns a callback that reads the running total. Use for "panel idle for 5 s does not block the main thread for more than X ms cumulatively". This is the metric that tracks scrolling smoothness.
- **`clickToVisible(trigger, target)`** - wall time between a click and the target locator becoming visible. Use for action latency: opening the detail panel, creating a task, expanding a card.

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

## Task Folder Contract

Each task folder contains:

- `job.json`: metadata (id, title, state, order, agent, cliType, model, sessionName).
- `prompt.md`: task description.
- `status.md`: generated review protocol.
- `logs/`: optional log files (CLI stdout/stderr lives here as `cli-output.log`).

States (ADR-0025: three-stage review pipeline):

```text
1-preparation -> 2-ready -> 3-progress -> 4-auto-review -> 5-human-review -> 6-completed -> 7-archive
```

`4-auto-review` is the orchestrator's lane (machine icon in the kanban): the `ReviewDecisionOrchestrator` decides reissue / accept-as-done / escalate. `5-human-review` is the lane that waits for the user (eye icon). The user always gets the final say on completion - the orchestrator never moves a task directly from `4-auto-review` to `6-completed`.

When the orchestrator accepts a task as done on its own judgment (advancing it to `5-human-review` rather than a human accepting it), it stamps the workspace provenance tag `orchestrator-moved` (label "Orchestrator: moved", text-only) on the card so a glance shows who advanced it. The tag is seeded in `TagRegistryService` and applied by `ReviewDecisionOrchestrator` on both the multi-aspect (`ProcessDoneAsync`) and single-verdict (`HandleAcceptAsDone`) accept paths; tasks a human accepts carry no such tag. `TagDriftRule` treats it as a provenance tag that survives aspect-concern reconciliation. The kanban lane labels themselves are display-only ("Ready", "Review", "Auto Review") and never rename the underlying `2-ready` / `5-human-review` / `4-auto-review` state keys, which are part of the on-disk + API contract.

**Evidence gate (ASS-764).** Before the orchestrator accepts a DONE run on its own judgment, two evidence rules can convert a would-be accept / accept-with-concerns into a reissue (or an escalation once the shared reissue budget is spent). The policy is the pure [`EvidenceGate`](backend/Services/Runner/EvidenceGate.cs), evaluated in `ProcessDoneAsync` after the parallel aspect run and handled by `HandleEvidenceGateAsync`:

1. **Visual-evidence requirement.** A task that is a `bug` taskType, carries a frontend/UI tag, or has a UI signal word in its title (`EvidenceGate.RequiresVisualEvidence`) must leave on-disk proof: an image under the task's `results/` folder (screenshot / Playwright capture) or a non-empty `results/review-evidence.jsonl` (`EvidenceGate.HasVisualEvidence`, fails closed on any IO error). A bare `Result: Success` with no such proof is not accepted - it is reissued with an explicit demand to capture a screenshot or e2e artifact before the next `[[TASK_DONE]]`, else stop with `[[TASK_BLOCKED:...]]`.
2. **Unclean tests-and-evidence is blocking.** When the `tests-and-evidence` aspect raises a concern (failing build / failing tests / missing evidence / `+0/-0` "test" commit), that category is blocking - reissue, never accept-with-concerns. This sits after the deterministic `CompletionGate`, which already runs before the aspects against the run's own close-out; the evidence gate runs after the aspects and upgrades the residual decision.

A task that ships its proof and passes a clean `tests-and-evidence` aspect is unaffected and flows normally to `5-human-review`. Coverage: `EvidenceGateTests` (pure policy) and `ReviewDecisionOrchestratorEvidenceGateTests` (the ASS-764 reproduction end to end).

Only tasks in `2-ready` or `3-progress` can be started via `/api/jobs/{id}/start`. New tasks default to `1-preparation`; the create endpoint accepts an optional `targetState` to land directly in `2-ready`.

Successful CLI runs move from `3-progress` to `4-auto-review` through application code. Failed or stopped runs stay in `3-progress` for inspection, restart, or continuation. The pre-ADR-0025 single `4-review` lane is migrated automatically on backend boot via `JobStateMachine.EnsureStateFoldersAndMigrate`.

### Orchestrator intake / Preparation step

Intake is the orchestrator's **Preparation** step: an opt-in, per-project check (`ProjectSettings.IntakeEnabled`) that vets every `2-ready` card before the coding runner is allowed near it. It runs in `IntakeHostedService` (a 20s background loop) against `IntakeRunner`, and is the substrate behind the board's "Preparation" lane.

- **Parallel, no code seat.** Intake is read-only-like — it never takes the single-active-run coding seat — so a tick drains *every* awaiting card in a project at once (oldest first, capped at `IntakeHostedService.MaxIntakePerProjectPerTick = 16`), not one per tick. The single-active-run boundary applies to `3-progress`, not to intake. This is what makes Preparation parallel-executable.
- **Substate, not a lane move.** Intake never moves the folder out of `2-ready`. It stamps a `phase` substate in `job.json` (`LifecyclePhases`, Ready-group): `human-ready` (or null = awaiting), `intake-running`, `intake-passed`, `intake-blocked`. A `lifecycle.json` sidecar (`LifecycleSnapshot`) records the verdict, the `intake-v1` check, and the context-load `ContextManifest`. Cards already stamped running/passed/blocked are skipped so a tick never re-runs a settled verdict.
- **UI.** The frontend renders an ephemeral `2-ready-intake` lane titled **Preparation** that is only visible when at least one card carries an intake substate (`readySplit.intake.length > 0`); it stays hidden otherwise. `human-ready` cards render in the normal Ready lane. The `2-ready` state key on disk is unchanged.

`IntakeRunner.Evaluate` is a pure outcome function returning the first non-Pass `IntakeOutcome` (else Pass), in order: blocked → already-done → consistency → duplicate → clarity → split. The outcomes:

| `IntakeOutcome` | Meaning | Resulting phase |
| --- | --- | --- |
| `Pass` | Executable; pickup gate opens. | `intake-passed` |
| `NeedsClarification` | Prompt too thin to run safely. | `intake-blocked` |
| `DuplicateCandidate` | Near-duplicate of a `2-ready` / recent review/completed card. | `intake-blocked` |
| `NeedsSplit` | Prompt bundles several independent units of work. | `intake-blocked` |
| `Blocked` | Hard out-of-scope / non-goal request. | `intake-blocked` |
| `AlreadyDone` | Done-precheck: the prompt declares the work already finished. | `intake-blocked`, then routed (see below) |
| `Inconsistent` | Consistency-check: card metadata is self-contradictory (empty goal/title, self-reference, or `blockedBy` set while queued in `2-ready`). | `intake-blocked` |

- **Consistency-check** (`CheckConsistency`) is peer-independent so it never false-positives on an incomplete peer scan: it only flags issues wrong regardless of which other tasks exist (placeholder/empty title, reference-to-self, blocked-while-ready). Prompt completeness is covered by `CheckClarity`; tag/reference completeness is recorded by the context manifest — together the four facets the spec lists (goal / prompt / references / tags) are each accounted for.
- **Context-load** (`IntakeRunner.BuildContextManifest`) resolves the card's cross-references against the known-task set and its `attachments/...` prompt tokens against files on disk, splitting each into resolved vs. missing, and captures its tags. Recorded in `LifecycleSnapshot.Context`; informational only — it does not gate pickup.
- **Done-precheck routing.** When intake returns `AlreadyDone`, `IntakeHostedService.RouteAlreadyDone` routes the card to `5-human-review` through the mandatory `HumanReviewEscalation` funnel (`HumanDecisionNeeded` category) for a person to confirm-and-complete. The orchestrator never auto-completes to `6-completed`. Going through the funnel (never a raw `MoveJob(... "5-human-review")`) is enforced by `HumanReviewVerdictDriftTest`. Routing is best-effort: a failed move leaves the card in `intake-blocked`, where the pickup gate already keeps the runner off it.

### Task organization rule: API first

Agents must organize tasks through the application API, not by directly creating, moving, deleting, or reordering folders in `agent-taskboard-workspace/projects/<projectKey>/`. This applies to **every agent surface**: the orchestrator-managed CLI runs, direct-from-VS-Code Codex / Claude Code / Copilot / Gemini sessions, and any ad-hoc shell session a human or LLM drives.

Use:

- `GET /api/watch-paths` to find the effective `watchPath`.
- `POST /api/jobs` with `CreateJobRequest` to create tasks.
- `POST /api/jobs/{jobId}/move?watchPath=...` to move tasks.
- `POST /api/jobs/batch-move` to move many tasks in one call (per-item atomic; failed items report `conflict` / `not-found` / `rejected` without rolling back items that already moved). This is the supported path for bulk restore / triage; do not fall back to shell loops over the single-item endpoint.
- `POST /api/jobs/{jobId}/restore-from-failed-pickup?watchPath=...` to lift a folder out of `3a-failed-pickup` back into `2-ready` and rename it to drop the `-pickup-failed-<utc>` suffix in one server-side step. Optional body `{"keepDeadLetterSlug": true}` retains the suffix. Idempotent (already-restored slugs return a 200 `no-op`); appends a `pickup-restored` row to `<workspace>/logs/pickup-failures.jsonl` for forensics. Use this instead of a manual `mv` + rename.
- `POST /api/jobs/reorder` to reorder tasks.
- `POST /api/jobs/{jobId}/move-to-top?watchPath=...` to promote a queued task.
- `POST /api/jobs/{jobId}/change-project?watchPath=...` to relocate a task between watched workspaces.
- `DELETE /api/jobs/{jobId}?watchPath=...` to delete tasks.
- `PUT /api/jobs/{jobId}/state?watchPath=...` plus the other `PUT /api/jobs/{jobId}/*` field-edit endpoints for content changes.

**Forbidden, even as a one-shot convenience:** `mv`, `rm`, `cp`, `mkdir`, `Move-Item`, `Remove-Item`, `Rename-Item`, or any other shell / filesystem command against a slug folder under `agent-taskboard-workspace/projects/<projectKey>/<lane>/`. Editing `state` inside a `job.json` by hand to "fix" a lane mismatch is the same bypass and is also forbidden. Filesystem state and the in-memory index diverge silently when these run, which is exactly what produced the 2026-05-09 zombie folder + 409 conflict. The architecture test [`backend.Tests/Architecture/JobFolderAccessIsolationTest.cs`](backend.Tests/Architecture/JobFolderAccessIsolationTest.cs) catches code-side bypasses; the LLM behavioural side is this rule.

If you need an operation the API does not expose, surface the gap as a queued task rather than reaching past the API. The previous gap "batch move / batch restore" is now covered by `POST /api/jobs/batch-move`; if you hit another missing surface, queue a new task rather than improvising a filesystem shortcut. See the API completeness audit at the top of [`task-access-api-layer-extraction`](docs/architecture-decisions.md) (ADR-0024).

Direct filesystem changes by application code itself are bounded by the same architecture test: only `backend/Services/Jobs/*`, `backend/Services/JobWatcherService.cs`, `backend/Services/Runner/CrashRecoveryService.cs`, and the `backend/Services/TaskAccess/` layer (today only the contract; phases 2-4 land the implementation) may construct lane folder paths or call `Directory.Move` / `Directory.Delete`. Everything else - endpoints, hosted services, analysis services - goes through the typed API. Backend migrations, recovery code paths, and tests that intentionally exercise the filesystem contract live behind that boundary; new direct-access call sites trip the architecture test on the way in.

See `docs/filesystem-contract.md` for full details.

## Code Conventions

- Frontend uses Angular signals for state.
- Frontend components are standalone; do not introduce NgModules.
- Keep the existing dark Catppuccin-inspired UI direction.
- Keep the detail view as a simple protocol view, without tabs or metrics grids unless the product direction changes.
- Prefer small, scoped changes and avoid rewriting unrelated code.

### Optimistic-UI for mutations + boot-hydrated catalog caches (ADR-0046)

Frontend mutations on durable user-owned fields (job title, model, CLI type, tags, drag-and-drop reorder, lane move) are **optimistic by default**: the local signal updates synchronously before the HTTP call leaves the browser, the call runs fire-and-forget, and a server error rolls the signal back and surfaces a toast. The canonical revert shape is snapshot → mutate → fire → on-error revert+toast, all visible at the call site (no generic wrapper). See `onAgentConfigCommit` in [frontend/src/app/features/job-detail/task-detail.ts](frontend/src/app/features/job-detail/task-detail.ts) and the kanban `applyOptimisticReorder` / `applyOptimisticMove` / `revertOptimistic*` pair in [frontend/src/app/services/task.service.ts](frontend/src/app/services/task.service.ts).

Durable reference lists (per-CLI model catalogs, tag registry, client identities, workspace registry) live in process-wide `*Store` services that pre-hydrate at app boot and are read synchronously from a signal. The first cache on the contract is [`CliCatalogStore`](frontend/src/app/services/cli-catalog.store.ts) (`hasFresh` / `modelsFor` / `ensure` / `refresh` / `invalidate`, 1 h TTL, in-flight dedupe); it is hydrated in `App.ngOnInit` via `cliCatalogStore.hydrateAll()`. Opening a model picker after boot is a synchronous render, not a round-trip.

Exceptions (stay synchronous with spinner): destructive operations (delete, bulk back-fill), and runner side effects (`start` / `continue`). For these, the truthful UI is the spinner; an optimistic mid-state would mislead the operator.

### Commit-Attribution-Regel: deterministic commit-to-task binding (ADR-0050)

Each task's git pane shows only the commits that actually belong to that task. Binding is **deterministic** - same inputs always yield the same attribution, no LLM in the default path. The engine is the pure [`CommitAttributionService`](backend/Services/Jobs/CommitAttributionService.cs); the post-step `RunCommitAttribution` in [`JobTransitionService`](backend/Services/Jobs/JobTransitionService.cs) runs it after agent execution (before `4-auto-review`) and persists results through `JobMutationService` (API-only, never a direct `job.json` write). Re-running is idempotent.

Rule order per candidate commit: (0) platform-stamped SHA from the session window -> attributed `automatic` confidence 1.0; (1) authored before the task window start -> excluded `outside-task-window`; (2) merge commit -> excluded `merge-commit`; (3) `chore(submodules): bump dev` style update-stable bump -> excluded `update-stable-bump`; (4) `chore(crash-recovery): rescue orphan changes for <other-task-id>` -> excluded `crash-recovery-of-other-task`; (5) working-dir prefix mismatch (not the dev checkout) -> excluded `other`; otherwise `automatic` with a computed confidence. Attribution is fully automatic - there is no operator override. Automatic results carry a confidence badge (design tokens, not magic colors). Data lands in `job.json` as `commits[]` (with `attribution` + `confidence`); the exclusion verdict shapes that chain but is not itself persisted onto `TaskInfo` or surfaced in the UI. An optional LLM fallback is reserved for confidence < 0.6 only and is toggleable; it is never on the default path.

### Menu surfaces are text-only

Context menus, dropdown menus, and overflow menus contain text only. No leading icons on menu items. This applies to `<app-menu>` and every consumer (tab right-click, card right-click, detail-header title menu, status-bar CLI / model pickers, project picker, markdown-editor mode toggle, protocol-pane overflow, chat model badge, future menu surfaces).

Icons remain allowed everywhere else: toolbars, status chips, lane glyphs, task-type chips, file-type icons in trees, the chat model badge's own pill glyph. The single allowed leading affordance on a menu row is `leadingGlyph`, the coloured-initial chip used by the project picker — it is intentionally not a decorative icon.

Rationale: visual calm and information density. A 5-item menu with 3 leading emoji is louder than the same menu in plain text. Operator preference is firm; do not re-introduce icons "just for this one menu".

Mechanics:

- `MenuRow` in [frontend/src/app/components/menu/menu.types.ts](frontend/src/app/components/menu/menu.types.ts) has no `icon` field. Adding one is a regression.
- The render template ([frontend/src/app/components/menu/menu.component.html](frontend/src/app/components/menu/menu.component.html)) intentionally renders only `leadingGlyph`, `label`, `hint`, and `trailingBadge`.
- Regression cover: [frontend/e2e/menu/menu-no-icons.spec.ts](frontend/e2e/menu/menu-no-icons.spec.ts) opens the tab context menu and asserts the panel contains no `img` / `svg` / `.app-menu__icon` element. Extend this spec when you add a new menu surface.

If a future destructive row truly needs a caution glyph, treat it as a per-case operator decision and propose it explicitly; do not slip it in as a "small exception".

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
