# Agent Software Studio

> **TEST / Hinweis an alle Agenten:** MEMORY hilft mir nicht - Memory ist pro Agent / pro Session und propagiert nicht. Ich brauche **immer einen Eintrag in einer Datei** (README, AGENTS.md, etc.), damit Änderungen für **alle Agenten gleichzeitig** wirken. Bitte keine agentenspezifischen Anpassungen vorschlagen - ich brauche **Standards**, die für viele Agenten funktionieren.

**Stop being the bottleneck.** A local Kanban board that feeds your coding-agent CLIs a continuous queue of work, using the subscriptions you already pay for, on the machine you already own.

![Board overview](docs/images/board-overview.png)

> .NET 10 backend + Angular 21 PWA. Job state lives in `.orchestrator/jobs/` folders on disk; the Task Access API fronts the filesystem so the runner, supervisor, frontend, and scripts read and mutate through one boundary. Runs tasks sequentially through Claude Code, Codex, GitHub Copilot, or Gemini.

---

## Security first

Agent Software Studio makes security work repeatable instead of heroic. A human reviewer can miss an edge case because they are tired, rushed, or carrying the context in their head. A queued agent can spend millions of tokens on the same class of review every time, write down what it checked, preserve evidence, and leave a durable protocol for human review.

That is the product bet: **with enough inference budget, the right process, and documented evidence, AI-assisted review can become more thorough than ordinary human-only security review.** The goal is not to trust a model blindly. The goal is to put frontier cyber capability inside a controlled workflow: clear task scope, project conventions, repeatable skills, logs, screenshots, summaries, and review gates.

This also makes a second pattern more realistic: for small, well-scoped internal libraries, it can be safer to regenerate or modernize the library behind a strong review process than to carry stale, under-tested legacy code forever. That is not a blanket rule. Highly sensitive primitives such as PKI, TLS, cryptography, authentication boundaries, and certificate handling need stronger human review, specialist skills, and often conservative patching rather than casual generation.

The external signal is getting hard to ignore. UK AISI's April 30, 2026 evaluation of OpenAI GPT-5.5 found it to be one of the strongest models they had tested on cyber tasks, with a 71.4% average pass rate on Expert-level advanced cyber tasks at a 50M-token budget, and the second model to complete one of their multi-step cyber-attack simulations end-to-end. AISI also notes that performance on the 32-step range continued to scale with inference compute. That supports the central premise here: security quality depends on model capability, sufficient token budget, and a process that captures what happened.

Source: [UK AISI evaluation of OpenAI GPT-5.5 cyber capabilities](https://www.aisi.gov.uk/blog/our-evaluation-of-openais-gpt-5-5-cyber-capabilities).

---

## The bottleneck is you

Modern coding agents can run for hours. They don't get tired. They don't context-switch. They just need a steady queue of work.

The bottleneck isn't the model. It's the human babysitting it: paste a prompt, watch it run, review, paste the next one. Every minute spent on that loop is a minute your subscription's token bucket sits idle.

```
  WITHOUT a queue                          WITH Agent Software Studio
  ───────────────                          ─────────────────────────

  you ──► prompt ──► agent ──► review      queue ──► agent ──► review
   ▲                            │            │ ▲                │
   │       (idle, you blink)    │            │ │                │
   └────────────────────────────┘            │ └────────────────┘
                                             │   (auto pickup)
   utilization: ~10–20% of the hour          │
                                             ▼
                                          next task
                                          
                                          utilization: ~95% of the hour
```

The board exists to make the queue the only thing you maintain. Tasks land in `2-ready`, the runner walks them through `3-progress → 4-review` automatically, and your role shrinks to **review**, the one part that actually needs you.

---

## Principles

**A layer on top of agents and software.** The product surfaces what the agents did and what changed in your software in one place. The top level is condensed (run summaries, commit counts, status badges); drill-down is always one click away (full activity log, diffs, tool calls). The full UX contract is in [docs/design-principles.md](docs/design-principles.md) and is the bar every protocol-layer change has to clear.

**Sequential within a project, never parallel.** One task at a time per project. No worktrees. No branch-per-task. No intra-project fan-out. Parallelism only exists *across* projects (different watch paths run independently).

**Security is a first-class workstream.** Security review is not a side quest at the end of a feature. It is a repeatable project-level activity with its own skills, evidence, history, and review surface. The board should make it normal to ask "when was this last reviewed, what was checked, what changed since then, and what evidence supports the conclusion?"

**Drift is a first-class project risk.** Long-running agentic work can drift between human intent, specs, tasks, jobs, ADRs, code, tests, design references, README, AGENTS, and marketing promises. The most important version is software drift: the actual source code, runtime behavior, tests, schemas, and module boundaries must stay aligned with the documented architecture. A project should be able to define a compact high-level architecture map with at most ten elements, then track drift per element.

**Use what you already pay for.** The runner drives **your** Claude Code, Codex, Copilot, and Gemini CLIs through their existing subscriptions. **No API keys. No per-token billing.** Your Pro/Max plan is the budget; the board's job is to use as much of it as productively as possible.

**Use existing coding agents, not a custom agent runtime.** Agent Software Studio deliberately sits above productized coding agents instead of rebuilding their agent loop against raw model APIs. Claude Code, Codex, Copilot, and Gemini already bundle planning, editing, tool use, approvals, authentication, model routing, and subscription economics. The app's job is queueing, lifecycle control, evidence capture, review handoff, and cross-CLI fallback. If a run gets awkward, the user can still drop into the native CLI or VS Code integration with the same subscription and provider-owned session artifacts where the provider exposes them.

Building a custom coding agent is not a forbidden idea. Many projects do it. It is out of scope for this product while the best price/performance sits in polished subscription coding agents, especially Codex and Claude Code. This boundary can be revisited if model economics or provider capabilities make API-native execution clearly better.

**Maximize token utilization, minimize bookkeeping.** Skip the things that burn time and tokens for marginal benefit:

| What it skips | Why |
|---|---|
| Worktrees | Spinning up a worktree per task triples I/O for no gain when work is sequential. |
| Virtualization / sandboxes | Adds startup latency and forces the agent to re-discover the workspace every run. |
| Cross-task orchestration | Workflow engines, branch coordination, merge bots. The sequential model removes this overhead by construction. |
| API-key-based execution | Subscriptions already cover this. Paying twice is silly. |
| Custom API-backed coding agent loop | Existing agents already package the hard product work: tools, approvals, session history, auth, model routing, and IDE fallback. |

The product is small on purpose. Any feature that pulls toward "let's run two agents on one project" is out of scope. That's where complexity (and bills) explode.

---

## What you see

### Board: every watched project, every state

![Board overview](docs/images/board-overview.png)

Six lanes, `1-preparation`, `2-ready`, `3-progress`, `4-review`, `5-completed`, and `6-archive`, are driven directly off the filesystem. Each card shows the CLI, the model, the task size, and last activity. The pill in the header says how many projects are running on auto-pickup.

### Detail view: task description + live protocol

![Detail view, task + protocol panes](docs/images/detail-protocol.png)

Click a card and you get the prompt on the left and the agent's protocol on the right. The protocol is a parsed, human-readable summary of what the agent has done so far, pulled from `status.md` and `cli-output.log` and re-rendered after every run.

### Three panes: task, protocol, live git

![Three-pane layout with git view](docs/images/detail-three-panes.png)

Toggle the Git panel to see what the agent actually changed in the project's working tree, file by file, while it works. No leaving the board to alt-tab into a terminal.

### Review handoff: what makes a task review-ready

![Review protocol in protocol view](docs/images/detail-quality-gate.png)

When a CLI run completes successfully, the application captures the run log, moves the task to `4-review`, writes a concise English protocol into `status.md`, and preserves review evidence such as screenshots under the job's `results/` folder.

Failed or stopped runs stay in `3-progress` so the user can inspect, restart, or continue them. The agent works on the selected task. The application owns pickup, continuation, stopping, state movement, protocol generation, and the one-active-task rule. That boundary is the point: the queue keeps moving without asking the model to decide what should run next.

---

## Deterministic orchestration over prompt trust

A second product principle, separate from the queue model: **the orchestrator is a deterministic arbiter, not a passive logger.** What the agent says about its own run is one input among several, never the only one.

This matters because prompt-based steering ("treat this as a continuation", "don't say done unless you actually did the work") fails silently. An agent that no-ops a follow-up after a session loss and replies "task done" used to slip through. The fix is structural:

1. **Hard signals from the agent.** Every prompt template asks the agent to end its run with one of `[[TASK_DONE]]`, `[[TASK_BLOCKED:<reason>]]`, `[[TASK_NEEDS_INPUT:<reason>]]`, or `[[TASK_NOOP]]`. These tokens are parsed from the output buffer and treated as authoritative. The full agent contract lives in [docs/agent-task-contract.md](docs/agent-task-contract.md).
2. **Deterministic post-run policy.** When the agent's report contradicts structural evidence (no edits, near-zero duration, after a recovery with a user follow-up), the orchestrator re-issues the work itself with a sharper framing instead of accepting the inconsistency. The decision tree is in `backend/Services/Runner/RunOutcomePolicy.cs` and is unit-tested as a matrix.
3. **An orchestrator voice in the chat.** The orchestrator is a first-class participant in the activity log (alongside `You` and the agent). When it re-issues a follow-up, accepts a heuristic verdict, or gives up after a retry, it says so in the chat so the user can see what the system decided and why. Heuristic fallback always surfaces a warning, so the user notices when the deterministic contract did not match.

The next chat surface extends this idea into a multi-actor conversation: user, task agent, orchestrator, supervisor, supporting agents, tools, and system warnings are separate participants. The design target is documented in [docs/mockups/chat-window-next-gen/](docs/mockups/chat-window-next-gen/); its integration plan makes the existing Activity Log, Trace mode, run timeline, side sheet, composer modes, and token/usage surfaces part of the migration instead of replacing them wholesale. The bridge slice has landed (`Frontend:NextGenChat` flag, shared `ConversationEvent` projection, fixtures, and the read-only Verbose Debug overlay); the remaining slices that wire the new renderer into the task Activity tab and the project side sheet are scoped in [docs/research/embedded-chat-integration-2026-05.md](docs/research/embedded-chat-integration-2026-05.md).

Prompt wording remains the easiest way to steer behavior, but it is not the load-bearing layer anymore. The product treats orchestrator-to-CLI communication as a core capability.

The next layer of this thinking is *supervision*: a meta-loop that watches the orchestrator's own job-pickup loop in real time, asks "is the agent on track, is anything stuck, should we intervene?", and writes its own continuous protocol. Implementation lives under [backend/Services/Supervisor/](backend/Services/Supervisor/) with a dedicated UI panel on each project page; auto-intervention stays opt-in. The full conceptual analysis (loop-to-loop control, communication contract, traceability) is in [docs/research/orchestrator-meta-loop-analysis-2026-05-04.md](docs/research/orchestrator-meta-loop-analysis-2026-05-04.md); the load-bearing decision is recorded as [ADR-0017](docs/architecture-decisions.md). A lower-frequency meta-cycle above the runner can pause at batch boundaries, inspect the system, write a structured report, then resume or queue follow-up work. Its current spec is [docs/mockups/orchestrator-meta-cycle/](docs/mockups/orchestrator-meta-cycle/) and the decision is [ADR-0022](docs/architecture-decisions.md). A stand-alone external review monitor (Layer 3) for stable lives at [scripts/supervisor/](scripts/supervisor/).

---

## Meta documentation, task evidence, and commits

Meta-level work is allowed to run as small, parallel CLI interactions when it is truly independent from the active coding task. Examples: analyze the orchestration model, update README or ROADMAP, write a research note under `docs/`, then commit that documentation immediately. These commits are normal product-memory commits. They do not violate the one-active-task rule because they do not execute task work inside a watched target project.

Recurring or manual meta-analyses are also product memory. Examples: "are we on track?", "what changed in the last few hours?", "which jobs are stale?", "does the queue match the roadmap?", "which docs drifted?", or "what should become follow-up work?" Their result should be a Markdown report for humans plus structured JSON when the app needs to aggregate, filter, or trend the findings. These reports belong in a project-level analysis area or in the relevant task evidence, depending on scope. They should reference raw evidence rather than copying entire logs, and any implementation follow-up becomes a normal queued task.

The orchestrator should use these reports to improve the steering layer over time. When multiple jobs show the same failure pattern, ambiguous prompt shape, recurring blocked reason, missing test expectation, or repeated CLI handling issue, a meta-analysis should point to the evidence and propose a README, AGENTS, task-contract, skill, or process update. That proposal must be visible and reviewable. The product should not secretly rewrite the instructions that agents rely on.

Agent-facing steering documents are product surface, not hidden implementation detail. A project page should make the relevant README, AGENTS, task contract, skills lookup, ADR index, and project-specific notes inspectable, with a shorter human summary on top that explains what the agents are being told and flags where the guidance looks stale, conflicting, or incomplete.

Task-level feedback is different. Security audits, code-review findings, task checks, screenshots, run protocols, and reviewer notes belong with the task evidence, usually in the watched project's `.orchestrator/jobs/<state>/<job>/` folder. If that evidence reveals new product work, create a normal queued task instead of burying the work inside the report.

Repositories should not stay dirty after a task is accepted. When a task reaches review or completion and its changes are accepted, the changed software and the task evidence should be committed promptly in the target repository and pushed unless the user has explicitly held the push back. The product should make uncommitted and unpushed task work visible so finished work does not quietly pile up on disk.

Direct-agent maintenance follows the same hygiene at the human session level: a small documentation, mockup, prompt, roadmap, or task-queue change should be committed and pushed as soon as it is coherent. That keeps project memory durable and avoids losing steering in a local checkout.

---

## Portable skills, not CLI-local silos

Skills are reusable specialist workflows: security review, Playwright visual verification, Angular UI work, backend API changes, log analysis, release preparation, and project-specific playbooks. They are **not** core lifecycle rules. Core orchestration is always active; skills are optional context that helps an agent do a situational workflow well.

The skill model has two layers:

1. **Central skill library.** Agent Software Studio owns the canonical skill library. Standard skills ship with the processor; project-specific skills are managed there too, scoped to one or more watched projects.
2. **Project lookup contract.** Each watched project should expose a small README or agent-instruction section that tells direct CLI agents where to find the relevant central skills. That keeps skills useful even when the user works directly in Codex, Claude Code, Copilot, or Gemini outside the orchestrator.

During a managed taskboard run, the orchestrator can attach selected skills to the prompt stack explicitly. During direct CLI work, the project's README acts as the common lookup point. Native CLI skill exports may be added later, but the Markdown lookup contract is the agent-neutral base.

The full concept lives in [docs/skills-architecture.md](docs/skills-architecture.md). The load-bearing decision is archived in [docs/architecture-decisions.md](docs/architecture-decisions.md).

---

## How it's wired

All job operations flow through the API. Direct filesystem mutation is reserved for the API host process.

The system is layered:

1. **Filesystem on disk.** The watched project's `.orchestrator/jobs/<lane>/<job>/` folders hold `job.json`, `prompt.md`, `status.md`, `logs/`, and `results/`. Disk stays the source of truth on cold start.
2. **Task Access API.** A typed software layer in the backend owns reads, lists, mutations, and lane transitions. It boots once, indexes every watched project's lane folders, watches the filesystem for external changes, serves cheap reads off the index, and accepts narrowly typed mutations. See [ADR-0024](docs/architecture-decisions.md) for the layer design and the queued `task-access-api-layer-extraction` work for the migration phasing.
3. **Services and clients consume the API.** The runner, the supervisor, the frontend PWA, the meta-cycle, and external scripts go through the API. They do not touch the lane folders directly. The same boundary mirrors mutations onto the [agent message bus](docs/agent-message-bus.md) so every cross-cutting structured signal lands in one observable timeline.

```
┌─────────────────────────────┐     ┌──────────────────────────────────┐
│  agent-taskboard/           │     │  Target project (e.g. C:\Proj\X) │
│  ════════════════           │     │  ═══════════════════════════════  │
│  App source code:           │     │  Where the agent works:          │
│  - backend/  (.NET 10 API)  │     │  - src/, lib/, ...               │
│  - frontend/ (Angular PWA)  │     │  - .orchestrator/                │
│  - docs/                    │     │    └── jobs/                     │
│  - .github/prompts/         │     │        ├── 1-preparation/        │
│                             │────►│        ├── 2-ready/              │
│  Hosts the Task Access API. │     │        ├── 3-progress/           │
│  Reads and mutates the      │     │        ├── 4-review/             │
│  target's jobs/ folder      │     │        ├── 5-completed/          │
│  through that one boundary. │     │        └── 6-archive/            │
└─────────────────────────────┘     └──────────────────────────────────┘
```

| Location | Contents |
|----------|----------|
| `agent-taskboard/` | App source, prompts, docs, Task Access API host |
| `<target-project>/.orchestrator/jobs/` | `job.json`, `prompt.md`, `status.md`, `logs/` per task |

One task processor, many targets. The board watches several projects in parallel; inside each project, work is strictly serial.

---

## Task Access API

The Task Access API is the canonical reference for every job operation. Agents, scripts, the frontend, the supervisor, and the meta-cycle all go through it. Direct filesystem reads or mutations are reserved for the API host process and for migrations or recovery work that deliberately exercise the on-disk contract.

Mutations require an `X-Client-Id` header so the layer can attribute the change to a registered client. Reads do not.

Canonical endpoints:

**Job lifecycle**

- `POST /api/jobs` - create a job. `CreateJobRequest` accepts `targetState` to land directly in `1-preparation` or `2-ready`.
- `POST /api/jobs/{id}/move?watchPath=...` - move a job to another lane.
- `PUT /api/jobs/{id}/state` - drive a job through a typed state transition.
- `POST /api/jobs/reorder` - reorder jobs within a lane.
- `DELETE /api/jobs/{id}?watchPath=...` - delete a job.
- `GET /api/jobs`, `GET /api/jobs/grouped`, `GET /api/jobs/{id}` - list and read.

**Job runner and content**

- `POST /api/jobs/{id}/start`, `POST /api/jobs/{id}/stop`, `POST /api/jobs/{id}/continue` - process lifecycle.
- `PUT /api/jobs/{id}/title`, `PUT /api/jobs/{id}/model`, `PUT /api/jobs/{id}/cli-type` - typed field updates.
- Git, attachments, run history, and per-run diff endpoints under the same `/api/jobs/{id}` group.

**Clients**

- `POST /api/clients/register` - register a client identity and obtain the `X-Client-Id` value.
- `GET /api/clients`, `GET /api/clients/{id}`, `DELETE /api/clients/{id}` - list, inspect, and retire clients.

**Supervisor and meta-cycle**

- `POST /api/supervisor/{project}/intervene/cancel-run`, `POST /api/supervisor/{project}/intervene/pause-pickup`, `POST /api/supervisor/{project}/intervene/force-fail`, `POST /api/supervisor/{project}/intervene/resume` - supervisor interventions.
- `GET /api/supervisor/{project}/meta-cycle` - meta-cycle status and recent reports.
- `GET /api/supervisor/{project}/observation`, `GET /api/supervisor/{project}/recent-events` - advisories, interventions, and recent activity for the project.

The wire shape for find / mutate is fixed in [`docs/schemas/task-find-result.schema.json`](docs/schemas/task-find-result.schema.json) and [`docs/schemas/task-mutation-request.schema.json`](docs/schemas/task-mutation-request.schema.json). The architectural decision is recorded in [ADR-0024](docs/architecture-decisions.md); the migration of the remaining direct-filesystem call sites is tracked under the queued task `task-access-api-layer-extraction`. Mutations are mirrored onto the [agent message bus](docs/agent-message-bus.md) as events.

---

## Running

Backend on `http://localhost:5030`, frontend on `http://localhost:4010`. Agents must use the `sh` variant.

```sh
./api.sh start                       # backend
npm start --prefix frontend          # or VS Code task "Frontend: Start"
```

### Dev vs. stable checkout

All code edits happen in the **dev** checkout. The stable checkout exists for reference and gets changes via `git pull` from `main`, never via direct edits. The dev checkout marks itself visually so the two never get confused:

- An orange "DEV" stripe is pinned to the top of the window.
- The PWA install icon and favicon use an orange variant with a "DEV" corner ribbon.
- The window title becomes `Agent Software Studio (DEV)`.

These markers activate when the backend serves `/api/environment` with `{ isDev: true }`, which it does iff a local-only `backend/appsettings.Local.json` file is present:

```json
// backend/appsettings.Local.json, gitignored, dev checkout only
{
  "Environment": {
    "IsDev": true
  }
}
```

The file is gitignored so it stays per-checkout. Stable lacks the file, so the same code produces the un-marked appearance there.

### Configuration

```json
// backend/appsettings.json
{
  "WatchPaths": [
    {
      "Name": "Runbook",
      "RootPath": "C:\\Projects\\Runbook\\App",
      "RepositoryPath": "C:\\Projects\\Runbook"
    },
    { "Name": "My Other Project", "RootPath": "C:\\Projects\\OtherApp" }
  ]
}
```

`RootPath` is the CLI working directory and the place where the board reads `<RootPath>/.orchestrator.yml` for `projectKey`. Jobs then resolve under `agent-taskboard-workspace/projects/<projectKey>/`.

`RepositoryPath` is optional. Use it when the Git repository root differs from the CLI working directory, for example a monorepo or a source app under a parent repository. Git status, diff, commits, and the VS Code handoff use `RepositoryPath`; when it is omitted, they fall back to `RootPath` and still ask Git for the work-tree top-level.

#### Orchestrator + supervisor toggles (UI-configurable)

The hosted-service flags that gate the auto-review orchestrator, the orchestrator-prep lane, the Layer-2 supervisor passes, the Layer-2.5 meta-cycle, and the auto-intervention policy used to require hand-editing `backend/appsettings.Local.json`. These are now reachable from the header `⋮` menu under "Orchestrator config". The drawer reads `GET /api/admin/config/orchestrator`, writes via `PUT` (X-Client-Id required), and the changes land in `backend/appsettings.Local.json` for the running checkout. All flags require a backend restart to take effect; the drawer surfaces a "Restart required" banner after every successful save.

### Job Organization Through The API

Agents and scripts must organize jobs through the application API, not by directly creating, moving, deleting, or reordering folders under `agent-taskboard-workspace/projects/<projectKey>/`.

Use the API for normal job operations:

- `GET /api/watch-paths` to discover the correct `watchPath`.
- `POST /api/jobs` with `CreateJobRequest` to create a job. Set `targetState` when a job should land directly in `1-preparation` or `2-ready`.
- `POST /api/jobs/{jobId}/move?watchPath=...` to move a job.
- `POST /api/jobs/reorder` to reorder jobs.
- `DELETE /api/jobs/{jobId}?watchPath=...` to delete a job.

Direct filesystem edits are reserved for backend implementation, migrations, recovery work, and tests that deliberately exercise the filesystem contract. They are not the normal operating path for agents. The API is the boundary that keeps ownership, client identity, validation, live updates, and future Task Access behavior in one place.

### Supported CLIs

Claude Code, Codex, GitHub Copilot, Gemini. The contract every CLI must satisfy, including process lifecycle, session model, model selection, quota probing, logging, and cancellation, is in [docs/supported-clis.md](docs/supported-clis.md).

### Keeping target projects in sync

When the agent task contract or folder schema changes, run the `/sync-target-instructions` prompt against each target.

---

## Docs

- [docs/README.md](docs/README.md) — **hierarchical lookup index** of every load-bearing document with a one-line description per file. Start here when you don't already know which doc to read.
- [AGENTS.md](AGENTS.md) — canonical agent instructions
- [ROADMAP.md](ROADMAP.md) — product direction, roadmap themes, and decision principles
- [PATHS.md](PATHS.md) — path conventions
- [prompts/runtime/](prompts/runtime/) — editable backend runtime prompt templates

The four most-asked-for individual documents (the index covers the full set):

- [docs/supported-clis.md](docs/supported-clis.md) — CLI integration contract
- [docs/filesystem-contract.md](docs/filesystem-contract.md) — job folder contract
- [docs/agent-task-contract.md](docs/agent-task-contract.md) — application and agent ownership boundary
- [docs/architecture-decisions.md](docs/architecture-decisions.md) — ADR archive with the load-bearing decisions
