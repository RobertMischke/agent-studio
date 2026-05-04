# Architecture Decisions

Only structural, load-bearing decisions land here. The bar is high: a decision belongs in this file when a future contributor would re-derive it the wrong way without it, or when there is a non-obvious thing the project deliberately does **not** do. Bug fixes, policy tweaks, defensive hardening, and individual feature choices stay in commits and code comments. If an entry reads like a changelog line, it is too small for this file.

Rough sizing test: an ADR captures a product or architecture *boundary* (sequential per project, deterministic orchestrator, four interaction modes), not a *fix* (re-issue policy details, capture-failure diagnostics, save-race guard). When a later decision changes a load-bearing one, supersede the old entry rather than deleting it.

Each entry uses the same shape:

```markdown
## ADR-NNNN - Title (YYYY-MM-DD)

**Decision.** One sentence.

**Context.** Why the question came up.

**Non-goals.** What this decision rules out, especially patterns that look attractive but are off the table.

**Reasoning style.** The lens future agents should reuse for similar questions.

**Implementation pointers.** Files / functions / commits that carry the decision today.

**Status.** Accepted | Superseded by ADR-MMMM | Deprecated.
```

Numbering is monotonic. Never reuse a number; never silently delete history.

---

## ADR-0001 - Sequential per-project, parallel across projects (2026-04-15)

**Decision.** A single coding task runs per project at a time. Different watched projects run independently and may execute concurrently. No intra-project parallelism, no worktrees, no branch-per-task.

**Context.** Modern coding agents can run for hours, but multi-agent fan-out inside one project produces conflicts that cost more than the parallel speed-up wins back. The product is a workbench for keeping one project moving, scaled across many projects.

**Non-goals.**
- Worktrees, sandbox-per-task, or any setup that triplicates I/O for sequential work.
- Workflow engines and multi-step orchestration on top of the queue.
- Branch creation, switching, merging, or worktree management by the app.
- Letting two coding agents edit one project at the same time.

**Reasoning style.** Hard product boundary, not a defaulted setting. Any feature request that implies intra-project parallelism is surfaced to the user before implementing.

**Implementation pointers.** [README.md](../README.md) "Sequential within a project, never parallel"; [ROADMAP.md](../ROADMAP.md) "Hard Boundaries"; [AGENTS.md](../AGENTS.md) "Product Goal & Non-Goals".

**Status.** Accepted.

---

## ADR-0002 - Deterministic orchestration over prompt trust (2026-05-02)

**Decision.** The orchestrator parses CLI output for typed signals (`[[TASK_DONE]]`, `[[TASK_BLOCKED:<reason>]]`, `[[TASK_NEEDS_INPUT:<reason>]]`, `[[TASK_NOOP]]`), applies a deterministic post-run policy, and speaks for itself in the activity log when it makes a decision. Prompt wording remains useful, but is not the load-bearing layer.

**Context.** A session-loss recovery run silently no-op'd a user follow-up and replied "task done" in 4.6 s. The orchestrator accepted that report. The user surfaced the failure and asked for the steering layer to live in code, not in the prompt.

**Non-goals.**
- Building behavior that relies on the agent obeying soft instructions in a prompt template.
- Hiding orchestrator decisions in backend logs only; the chat must surface them.
- Adding an LLM call to classify outcomes when a hardcoded sentinel can.

**Reasoning style.** Pure-function libraries with their own test matrix per concern: parser, policy, meta channel. Heuristics are allowed only as a fallback and must announce themselves with a meta message so the user sees when the deterministic contract did not match.

**Implementation pointers.** [backend/Services/Runner/AgentOutcomeAnalyzer.cs](../backend/Services/Runner/AgentOutcomeAnalyzer.cs); [backend/Services/Runner/RunOutcomePolicy.cs](../backend/Services/Runner/RunOutcomePolicy.cs); [backend/Services/Runner/OrchestratorChatLog.cs](../backend/Services/Runner/OrchestratorChatLog.cs); [docs/agent-task-contract.md](agent-task-contract.md) "Output Contract"; [README.md](../README.md) "Deterministic orchestration over prompt trust".

**Status.** Accepted.

---

## ADR-0003 - Four follow-up interaction modes; Extend writes a prompt-N.md timeline (2026-05-02)

**Decision.** `ContinueJobRequest.Mode` is a typed string with four values: `continue`, `steer`, `extend`, `newTask`. Each value selects a prompt frame in `RunPlanner.BuildContinuePrompt`. Extend mode also writes a new `prompt-N.md` (1-based) into the same job folder; the original `prompt.md` is never overwritten. The Task Description pane renders the timeline blog-style.

**Context.** The user's actual workflow is a living chat session: continue, course-correct, extend the task, or open a new sub-task without losing context. Treating every follow-up as a generic next message produced "I'll wait for your request" no-ops on extensions. A single mode value carries the user's intent through the whole run.

**Non-goals.**
- Spawning a new job folder per extension. Same folder, blog-style timeline.
- Editing `prompt.md` in place when the user extends. Extensions are append-only.
- Hiding the mode behind a hotkey or hover menu. The pill row is intentionally loud.

**Reasoning style.** UX state is backend state too: the mode is a typed wire field, not a frontend-only flag. Extensions are evidence on disk, not just turns in a chat buffer, so any restart, log inspection, or Haiku summary can read the full timeline.

**Implementation pointers.** [backend/Models/JobModels.cs](../backend/Models/JobModels.cs) (`ContinueModes`, `JobPromptHistoryEntry`); [backend/Services/Runner/RunPlanner.cs](../backend/Services/Runner/RunPlanner.cs) `BuildContinuePrompt`; [backend/Services/TaskRunnerService.cs](../backend/Services/TaskRunnerService.cs) prompt-history append; [frontend/src/app/components/job-detail/protocol-pane/protocol-pane.component.ts](../frontend/src/app/components/job-detail/protocol-pane/protocol-pane.component.ts) (mode pills); [frontend/src/app/components/job-detail/prompt-pane/prompt-pane.component.html](../frontend/src/app/components/job-detail/prompt-pane/prompt-pane.component.html) (history block).

**Status.** Accepted.

---

## ADR-0004 - Behavioral changes need a live probe (2026-05-02)

**Decision.** Any change to a file under `prompts/runtime/` or to a code path that determines what the CLI actually does (driver flags, output parser, post-run policy) is a behavioral change against the agent, not a textual change. Unit tests on rendered string content or pure-function inputs are necessary but not sufficient. Before claiming such a change is safe, run the `@billable` `claude-hello-world.spec.ts` (or the per-CLI equivalent) end-to-end and confirm the agent produces real work. Structural unit-test guards (e.g. "user task header appears before run-context header") sit alongside the live probe; they prevent silent regressions of the structural property but do not replace the probe.

**Context.** Two production-breaking regressions slipped through pure-function tests in this conversation: a session-capture loop and a prompt-restructure that made every Claude run exit in 3 s with "I'll wait for your request". Both passed unit tests because the tests verified template strings and planner outputs, not Claude's behavior on the resulting prompt. The user surfaced both as "tasks aren't being processed" and asked why tests didn't catch it.

**Non-goals.**
- Hand-waving the test gap as "integration is hard". The `@billable` probe exists, costs one Haiku call (~10 s), and is the ground truth.
- Shipping prompt-template edits without verification because "it should work". If the probe cannot be run in the current session, say so explicitly; do not silently ship.
- Moving every behavioral concern into Playwright. Pure-function tests stay; they pin the structural shape so the live probe only has to verify the wiring.

**Reasoning style.** Two-layer regression always has two layers of fix: a structural guard and a behavioral probe. Pure-function tests pin properties (order of content, planner decisions, sentinel parsing); the live probe is the only thing that proves the resulting prompt actually drives the CLI.

**Implementation pointers.** [AGENTS.md](../AGENTS.md) "Prompt-template changes: live probe required"; [frontend/e2e/claude-hello-world.spec.ts](../frontend/e2e/claude-hello-world.spec.ts); structural assertions in [backend.Tests/TaskRunnerPromptTests.cs](../backend.Tests/TaskRunnerPromptTests.cs).

**Status.** Accepted.

---

## ADR-0005 - Portable skills use a central library plus project lookup contract (2026-05-02)

**Decision.** Skills are stored and managed as portable workspace knowledge in Agent Task Processor, while each watched project exposes a README or agent-instruction lookup section so direct CLI sessions can discover the same skills.

**Context.** The user wants the orchestrator to be the main work surface, but also wants to move into direct Codex, Claude Code, Copilot, Gemini, or VS Code sessions without losing the reusable specialist workflows built up in the task processor. A skill system that only works during managed taskboard runs would create two disconnected worlds.

**Non-goals.**
- Making core orchestration depend on probabilistic skill activation.
- Storing the canonical skill source separately per CLI.
- Assuming every CLI has the same native skill mechanism.
- Requiring users to remember skill paths manually when working in a child project.

**Reasoning style.** Separate ownership from reach. The task processor owns the canonical skill library and deterministic attachment during managed runs. Watched projects own a small lookup contract that makes those skills visible to direct CLI sessions. Native CLI exports are adapters, not the source of truth.

**Implementation pointers.** [docs/skills-architecture.md](skills-architecture.md); [README.md](../README.md) "Portable skills, not CLI-local silos"; [AGENTS.md](../AGENTS.md) "Portable Skills"; existing proto-skill files in [docs/cli-skills/](cli-skills/).

**Status.** Accepted.

---

## ADR-0006 - Orchestrator runs as a separate CLI process with its own model and feed (2026-05-02)

**Decision.** When the orchestrator needs to make a decision on the user's behalf (today: an agent emits `[[TASK_NEEDS_INPUT]]` while the runner is in auto mode), it spawns the configured Claude CLI in one-shot JSON mode (`--output-format json`), captures the result text plus token usage, and writes the decision to a per-project `orchestrator.jsonl` log. The orchestrator's model is a per-project setting, default `claude-opus-4-7`. It does **not** share a session with the active task agent and does **not** use Anthropic's HTTP API directly.

**Context.** The orchestrator started as a passive parser of CLI output. Auto-mode workflows expose situations where it must actively decide (which follow-up to send when the agent asks a question, whether the user's queued draft is still consistent, whether to override a stale plan). The user wants those decisions to be high-quality (default to the strongest model), visible (its own feed with token counts), tunable (model is per-project), and overridable (the user can intervene on any decision and the override flows through the existing Continue path). The product also commits to "use what you already pay for" - subscriptions, not API keys - so the orchestrator must not introduce a new billing surface.

**Non-goals.**
- Calling Anthropic's HTTP API directly. The product's billing model is CLI subscriptions (Pro / Max / Team / Enterprise); a hidden API path would create real dollar cost where the user expects zero, and would require an API key the user does not need to provide today.
- Sharing a session with the active task agent. Every orchestrator decision is a clean one-shot call so its context is exactly the situation it is asked to judge - no leakage from prior task turns, no risk of the agent seeing orchestrator reasoning as user input.
- A long-lived orchestrator process. Each decision is a fresh CLI invocation; prompt caching at the Anthropic side handles repeated framing without us managing a daemon.
- Treating the orchestrator's theoretical API cost as a bill. The cost number in the UI is a comparison metric only; the disclaimer in [TokenSummary](../backend/Services/Runner/TokenSummary.cs) is load-bearing copy and must stay.
- Auto-firing orchestrator decisions on manual-mode projects. The trigger is explicitly gated on `auto-continuous` / `auto-single`. Manual mode keeps the question with the user. See [ProjectRunner.IsAutoMode](../backend/Services/Runner/ProjectRunner.cs).

**Reasoning style.** Treat orchestrator activity as first-class evidence on disk. The orchestrator log (`<watchPath>/.orchestrator/orchestrator.jsonl`) is the single source of truth: decisions, watchdog actions, queued follow-ups, and user overrides all land there with kind / topic / token usage / optional reasoning. Pure functions wherever possible (`TokenPricing`, `Watchdog.DecideState`, `RunOutcomePolicy.Decide`); the runner applies the side-effects. The frontend renders the same evidence the same way for everyone (orchestrator feed, project detail, token-summary block) so the user trusts what they see.

**Implementation pointers.** [backend/Services/Runner/OrchestratorRunner.cs](../backend/Services/Runner/OrchestratorRunner.cs) (the one-shot CLI invoker), [backend/Services/Runner/OrchestratorLog.cs](../backend/Services/Runner/OrchestratorLog.cs), [backend/Services/Runner/TokenPricing.cs](../backend/Services/Runner/TokenPricing.cs), [backend/Services/Runner/TokenSummary.cs](../backend/Services/Runner/TokenSummary.cs); the auto-mode hook in [ProjectRunner.OnCliFinishedAsync](../backend/Services/Runner/ProjectRunner.cs); the override endpoint in [RunnerEndpoints.cs](../backend/Endpoints/RunnerEndpoints.cs); the per-project model setting in [ProjectSettings.OrchestratorModel](../backend/Models/JobModels.cs); [frontend/src/app/components/orchestrator-feed.ts](../frontend/src/app/components/orchestrator-feed.ts), [frontend/src/app/components/project-detail.ts](../frontend/src/app/components/project-detail.ts), [frontend/src/app/components/token-summary-block.ts](../frontend/src/app/components/token-summary-block.ts).

**Status.** Superseded in part by ADR-0007: the "no long-lived process" non-goal is overturned. The session-id is now persisted per project and decisions resume the session via `claude -r`. The "no Anthropic API direct path" non-goal is still in force.

---

## ADR-0007 - Per-project long-lived orchestrator session for warm context (2026-05-02)

**Decision.** Each watched project owns one Claude session that is booted on app start and reused for every later orchestrator decision via `claude -r <sessionId>`. The session id, boot prompt preview, boot reply preview, and cumulative token totals are persisted at `<watchPath>/.orchestrator/orchestrator-session.json`. The session id and what was loaded on boot are inspectable in the UI; the user can also resume the session in their own terminal via the displayed `claude -r <id>` command.

**Context.** ADR-0006 ruled out a long-lived orchestrator process to keep things simple, on the assumption that prompt caching at the Anthropic side would handle repeated framing. In practice that left every decision as an opaque one-shot call: no warm project context, no inspectable "what does the orchestrator know about us", and token usage that piled up without being attributable to one running ledger. The user said it directly: *"ich hätte gerne irgendwie sowas wie: 'Was hast du alles gelesen? Wie wurdest du initialisiert?' Also soll nicht einfach nur im luftleeren Raum da sein. Am liebsten hätte ich glaube ich eine langlebige CLI Session, damit ich mich mit dem Ding wohlfühle."*

**Non-goals.**
- Calling Anthropic's HTTP API directly. Subscriptions still bill the work; ADR-0006's primary non-goal stays.
- A long-lived orchestrator *process*. We invoke `claude -p ... -r <id>` per decision and let the process exit; the session lives on Anthropic's side, not ours. This keeps the runtime stateless and the failure mode visible (one process per decision, exits cleanly).
- Sharing one session across projects. Each project's orchestrator has its own session so context does not leak; per ADR-0006 the orchestrator's model is also per-project.
- Hiding the boot. Boot prompt + reply + session id are surfaced verbatim in the project detail panel so the user can audit what the orchestrator was told and what it acknowledged.
- Eager re-boot on every backend restart. The persisted session id is reused; we only re-boot when no session is on disk, or when a resume call returns a "session not found" style error (we then drop the stale id and fall back to a one-shot for that decision).

**Reasoning style.** Trust + transparency over runtime cleverness. The session-on-disk model fits the same pattern as job session UUIDs (ADR-0003 chain): write what the CLI gave us, resume by id, fall back to a fresh boot when the id is stale. Keep the boot small (project README/AGENTS/ROADMAP truncated, recent log entries) so even on Opus the boot is a few cents at most.

**Implementation pointers.** [backend/Services/Runner/OrchestratorSession.cs](../backend/Services/Runner/OrchestratorSession.cs) (record + store + AccumulateUsage); [backend/Services/Runner/OrchestratorRunner.cs](../backend/Services/Runner/OrchestratorRunner.cs) (`DecideAsync` for boot, `ResumeAsync` for follow-ups, `CapturedSessionId` on the result); [backend/Services/Runner/ProjectRunner.cs](../backend/Services/Runner/ProjectRunner.cs) (`BootOrchestratorSessionAsync`, the resume path in the auto-mode NeedsInput hook, stale-session fallback); boot kicked off at app start by [TaskRunnerService.ExecuteAsync](../backend/Services/TaskRunnerService.cs); read endpoint `/api/runner/{name}/orchestrator-session` in [RunnerEndpoints.cs](../backend/Endpoints/RunnerEndpoints.cs); UI in [frontend/src/app/components/project-detail.ts](../frontend/src/app/components/project-detail.ts) "Orchestrator session" group.

**Status.** Accepted.

---

## ADR-0008 - Auto-loop circuit breaker for orchestrator-answered NEEDS_INPUT (2026-05-02)

**Decision.** When the project runner is in an auto mode and the active agent emits `[[TASK_NEEDS_INPUT:...]]`, the orchestrator answers on the user's behalf and re-issues the work as a Continue. The loop is bounded per job by `StuckLoopBudget` - by default `MaxIterations = 5` and `MaxOrchestratorTokens = 200_000` (configurable via `StuckLoop:*` in appsettings). When either ceiling is hit the loop stops, the chat receives a `[circuit-breaker]` orchestrator meta line, and the question is left for the user. Loop state is per-job, in memory, reset on any non-NeedsInput outcome (Done/Blocked/etc.).

**Context.** The user's product goal is "I drop a problem in and the system works on it stably until it's done, even when the agent has questions along the way." None of the four CLIs we drive (Claude Code, Codex, GitHub Copilot, Gemini) and none of the agentic loops we surveyed (aider, opencode) auto-answer the model's questions on the user's behalf - they all surface the question and stop. This product is deliberately different on that axis: in auto mode the orchestrator IS the user. That choice is only safe if the loop is bounded; without it a stuck conversation could spend the entire CLI subscription quota on a question the agent and the orchestrator cannot resolve together.

**Non-goals.**
- Token budget on the *agent's* run. The watchdog bounds wall-clock silence; iteration counts here are about how many *orchestrator* decisions are spent on one job, not how long the agent runs per turn.
- Persisting loop state across backend restart. A restart is itself a recovery boundary; the user can decide whether to resume the loop manually after one. (Persistence would require a sidecar file and the failure modes - stale counts on restart, contention with pending-intent.json - aren't worth the wins yet.)
- Hiding the loop. The kanban card shows an `auto-loop N/M` pill that turns amber at 80% of the iteration cap, the chat carries the orchestrator's per-decision meta line ("loop 2/5: ..."), and the orchestrator log records both the decision and the eventual circuit-break event.
- Auto-resume after circuit-break. Once the loop stops, the user is the only one who can re-engage the orchestrator on this job. Otherwise the safety net is no net.

**Reasoning style.** Reference the closest OSS analogues for default sizing, then write the rule down so reviewers can argue with it. aider ships `max_reflections = 3` for its retry-after-output-didn't-satisfy loop ([base_coder.py](https://github.com/Aider-AI/aider/blob/main/aider/coders/base_coder.py)). The Claude Code Agent SDK's canonical "circuit breaker" example uses `max_turns = 30` for a different problem (one agent's tool-use round trips, not orchestrator decisions on top of an agent). Our 5/200k pair is in the same shape: generous enough for a normal multi-turn back-and-forth, tight enough that a stuck loop stops at a few cents of Opus spend. Cache-read tokens are excluded from the budget because subscription quota does not bill them.

**Implementation pointers.** [backend/Services/Runner/StuckLoopGuard.cs](../backend/Services/Runner/StuckLoopGuard.cs) (pure-function `Empty` / `Next` / `Decide` / `FormatBreakerMessage`); [backend.Tests/StuckLoopGuardTests.cs](../backend.Tests/StuckLoopGuardTests.cs) (8 tests locking the contract); [backend/Services/Runner/ProjectRunner.cs](../backend/Services/Runner/ProjectRunner.cs) (`_stuckLoops` per-job counter, gate at the entry of `RunOrchestratorDecisionAsync`, advance after each call, reset in `OnCliFinishedAsync` on non-NeedsInput outcomes); [backend/Services/TaskRunnerService.cs](../backend/Services/TaskRunnerService.cs) (`LoadStuckLoopBudget` from `StuckLoop:*`, `GetStuckLoopStateForJob` for the API surface); UI snapshot via `WithRuntime` in [JobEndpointHelpers.cs](../backend/Endpoints/Jobs/JobEndpointHelpers.cs) and the `auto-loop N/M` pill in [job-card.ts](../frontend/src/app/components/job-card.ts).

**Status.** Accepted.

---

## ADR-0009 - Global orchestrator above per-project orchestrators (2026-05-02)

**Decision.** A singleton "global orchestrator" Claude session is booted at app start in addition to the per-project orchestrators (ADR-0007). The global session knows the watched-project roster and current job-state counts; it exists to answer cross-project questions (which project is starving, what's happening across the board, where should the user look first) and explicitly does NOT reach into any single task's NEEDS_INPUT decision - that stays the per-project orchestrator's job. The session id is persisted at `<TaskRepository>/.runtime/global-orchestrator-session.json`, exposed at `GET /api/runner/global/orchestrator-session`, and shown in the UI as a card at the top of the orchestrator panel.

**Context.** ADR-0007 deliberately kept orchestrator sessions per-project so context never leaks across projects. That works for "what should this single agent do next?" but leaves a gap for the user's cross-project questions ("welche Projekte brauchen jetzt Aufmerksamkeit?"). Without a global view, every cross-project question forces the user to scan all projects manually. A second session, sitting above the per-project ones, fills that gap without violating the per-project isolation: the global session has only roster-level facts, never the contents of a single project's docs.

**Non-goals.**
- Sharing the global session WITH any per-project session. They are independent Claude sessions; the global one cannot peek inside a project session and vice versa. ADR-0007's isolation is preserved.
- Letting the global orchestrator decide on a single agent's NEEDS_INPUT. That stays per-project; the boot prompt explicitly tells the global model to defer there.
- Calling Anthropic's HTTP API directly. Same non-goal as ADR-0006/0007: subscriptions still bill the work.
- A long-lived global *process*. We invoke `claude -p ... -r <id>` per question and let the process exit.
- Replacing the per-project orchestrator. The per-project sessions stay; the global one is additive.

**Reasoning style.** Mirror what already worked for per-project (ADR-0007): persist the session id on disk, reuse on restart, fall back to a fresh boot when the id is stale. Keep the boot prompt small (project roster + per-state job counts) so even on Opus the boot is a few cents. The boundary between "global" and "per-project" is the same boundary the user sees in the UI - one card for the board, one card per project.

**Implementation pointers.** [backend/Services/Runner/GlobalOrchestratorSession.cs](../backend/Services/Runner/GlobalOrchestratorSession.cs) (record + store + AccumulateUsage); [backend/Services/Runner/GlobalOrchestratorBootstrap.cs](../backend/Services/Runner/GlobalOrchestratorBootstrap.cs) (boot prompt builder + boot flow); kicked off in [TaskRunnerService.ExecuteAsync](../backend/Services/TaskRunnerService.cs) right after the per-project boots; read endpoint in [RunnerEndpoints.cs](../backend/Endpoints/RunnerEndpoints.cs); UI in [frontend/src/app/components/global-orchestrator-card.ts](../frontend/src/app/components/global-orchestrator-card.ts), mounted at the top of [project-detail.ts](../frontend/src/app/components/project-detail.ts) and [orchestrator-feed.ts](../frontend/src/app/components/orchestrator-feed.ts).

**Status.** Accepted.

---

## ADR-0010 - Deliberate kills surface as 'stopped', not 'failed' (2026-05-03)

**Decision.** Every place that calls `Process.Kill` on a CLI subprocess records a `RunStopReason` (UserStop / FollowupPause / Watchdog / Cancelled) before issuing the kill. `MonitorProcessAsync` feeds `(exitCode, reason)` through the pure `RunStatusClassifier`, which maps any non-`None` reason to the new `status = "stopped"` regardless of exit code. Only natural exits keep the legacy `completed` / `failed` mapping. The frontend treats `stopped` as a calm pill and skips the failure modal.

**Context.** On Windows, `Process.Kill(entireProcessTree:true)` deterministically returns `exitCode = -1`. The legacy classifier was a single inline `exitCode == 0 ? "completed" : "failed"`, so user pauses, the Pause-&-Send choreography (UI calls `/stop` then `/continue`), and the silence watchdog all surfaced in the UI as `"Task execution failed with exit code -1"` modals. The user reported this directly, both for an explicit pause and for the in-flight Pause-&-Send case. The reason field is the only honest signal of "we killed this on purpose"; without it the backend cannot tell its own kill apart from a real CLI crash.

**Non-goals.**
- Letting the watchdog or the host-shutdown cancellation path produce `failed`. Both are deliberate kills and must read as `stopped` so users do not chase phantom crashes.
- Encoding the stop reason in the persisted `RunRecord`. The reason is in-memory state used purely for classification at exit time; what survives on disk is the resulting `status`.
- A separate `cancelled` status alongside `stopped`. Older in-memory `CliExecution` snapshots may still carry the legacy `cancelled` value, and the frontend renders both with the same calm pill, but new code only emits `stopped`.

**Reasoning style.** Same shape as `Watchdog.DecideState` and `RunCompletionPolicy`: extract the rule into a pure helper with its own test matrix so the next contributor cannot quietly re-inline `exitCode == 0 ? completed : failed`. Reason metadata sits next to the kill, never derived afterwards from heuristics on log lines.

**Implementation pointers.** [backend/Services/Runner/RunStatusClassifier.cs](../backend/Services/Runner/RunStatusClassifier.cs) (enum + statuses + pure classifier); [backend.Tests/RunStatusClassifierTests.cs](../backend.Tests/RunStatusClassifierTests.cs) (matrix); [backend/Services/Cli/CliExecutionServiceBase.cs](../backend/Services/Cli/CliExecutionServiceBase.cs) `Stop` + `MonitorProcessAsync`; matching changes in [backend/Services/CopilotCliService.cs](../backend/Services/CopilotCliService.cs); watchdog kill in [backend/Services/Runner/ProjectRunner.cs](../backend/Services/Runner/ProjectRunner.cs); API hint in [backend/Endpoints/Jobs/JobRunnerEndpoints.cs](../backend/Endpoints/Jobs/JobRunnerEndpoints.cs); frontend skip-modal in [frontend/src/app/components/job-detail.ts](../frontend/src/app/components/job-detail.ts) `applyExecutionState`; Pause-&-Send sends `reason=followup` in the same file; E2E in [frontend/e2e/stop-no-error-modal.spec.ts](../frontend/e2e/stop-no-error-modal.spec.ts).

**Status.** Accepted.

---

## ADR-0011 - Orchestrator chat uses canonical scope sessions with visible memory (2026-05-03)

**Decision.** The persistent Orchestrator Chat is the user-facing surface for the existing canonical orchestrator session of its scope: the global orchestrator for board-level chat and the project orchestrator for project-level chat. It is backed by a durable event log and an inspectable memory snapshot. Forks are explicit, short-lived research or recovery branches that report back to the canonical session; they do not become peer orchestrators.

**Context.** The user wants an optional always-available orchestrator chat that feels alive across days, can explain what happened in the software, understands the application it controls, and can eventually steer the app. The central ambiguity was whether the chat should be a separate instance, whether each app needs two orchestrators, and whether "keeping it alive" means pinging a model. Existing ADRs already established per-project long-lived sessions (ADR-0007) and a global orchestrator above them (ADR-0009). This decision connects those sessions to the chat surface and adds the missing memory contract.

**Non-goals.**
- A second peer project orchestrator just for chat. The chat talks to the canonical project orchestrator so there is one owner of project memory and decisions.
- A permanently running model process. Continuity comes from session id, event log, and memory snapshot; the CLI process may start and exit per interaction.
- Blind keep-alive pings. Local freshness checks are fine; LLM calls should happen for user questions, auto-mode decisions, or meaningful memory refreshes.
- Hidden memory. The memory snapshot must be visible, refreshable, and rebuildable from local evidence.
- Free-form UI automation. The orchestrator can control the app only through typed actions validated by normal backend policy.
- Forks that silently replace the canonical session. Forks produce evidence or proposals that the canonical orchestrator may absorb.

**Reasoning style.** Treat user trust as a context observability problem. The user does not need a model process that never sleeps; they need to know which orchestrator they are talking to, what it remembers, where that memory came from, what it decided, and which app action it is proposing. Use deterministic memory assembly first, then model judgment only where compression or reconciliation adds value.

**Implementation pointers.** [docs/orchestrator-chat.md](orchestrator-chat.md) for product shape, memory model, first slice, and open questions; [README.md](../README.md) "Persistent orchestrator chat"; [ROADMAP.md](../ROADMAP.md) "Persistent Orchestrator Chat"; [docs/design-principles.md](design-principles.md) "The orchestrator has visible memory"; existing session primitives in [backend/Services/Runner/OrchestratorSession.cs](../backend/Services/Runner/OrchestratorSession.cs), [backend/Services/Runner/GlobalOrchestratorSession.cs](../backend/Services/Runner/GlobalOrchestratorSession.cs), and [backend/Services/Runner/OrchestratorLog.cs](../backend/Services/Runner/OrchestratorLog.cs).

**Status.** Accepted.
