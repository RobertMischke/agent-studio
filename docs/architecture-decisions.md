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

**Decision.** Skills are stored and managed as portable workspace knowledge in Agent Software Studio, while each watched project exposes a README or agent-instruction lookup section so direct CLI sessions can discover the same skills.

**Context.** The user wants the orchestrator to be the main work surface, but also wants to move into direct Codex, Claude Code, Copilot, Gemini, or VS Code sessions without losing the reusable specialist workflows built up in the task processor. A skill system that only works during managed taskboard runs would create two disconnected worlds.

**Non-goals.**
- Making core orchestration depend on probabilistic skill activation.
- Storing the canonical skill source separately per CLI.
- Assuming every CLI has the same native skill mechanism.
- Requiring users to remember skill paths manually when working in a child project.

**Reasoning style.** Separate ownership from reach. The task processor owns the canonical skill library and deterministic attachment during managed runs. Watched projects own a small lookup contract that makes those skills visible to direct CLI sessions. Native CLI exports are adapters, not the source of truth.

**Implementation pointers.** [docs/skills-architecture.md](skills-architecture.md); [README.md](../README.md) "Portable skills, not CLI-local silos"; [AGENTS.md](../AGENTS.md) "Portable Skills"; existing proto-skill files in [docs/cli-skills/](cli-skills/); v1 project-level readiness flow at [backend/Services/SkillReadinessService.cs](../backend/Services/SkillReadinessService.cs), [backend/Endpoints/SkillReadinessEndpoints.cs](../backend/Endpoints/SkillReadinessEndpoints.cs), and [frontend/src/app/components/project-skill-readiness-section.ts](../frontend/src/app/components/project-skill-readiness-section.ts).

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

## ADR-0011 - CLI-process spawn boundary: prefer the underlying .exe (Claude only for now), tighten test coverage of the runner+watchdog chain (2026-05-03)

**Decision.** Two related changes, scoped narrowly to what we have evidence for:

1. **Claude only:** when the runner builds the `ProcessStartInfo` for Claude, it rewrites `<prefix>\claude.CMD` to the underlying `<prefix>\node_modules\@anthropic-ai\claude-code\bin\claude.exe` via [`ClaudeCliService.ResolveCmdShimToExe`](../backend/Services/Cli/ClaudeCliService.cs). The `.exe` is a single Windows PE bundle Anthropic ships; calling it directly removes the implicit `cmd.exe /c "..."` wrap that the `.CMD` invocation triggers. This is a defensible structural simplification and a removed variable for future hang investigations - it is **NOT** a proven fix for the original "agent silent after init" symptom (see Caveat below).
2. **All CLIs:** add an integration-test suite that exercises the runner + watchdog chain. Two test files live side by side:
   - [`CliSpawnIntegrationTests.cs`](../backend.Tests/CliSpawnIntegrationTests.cs): five probes that drive the **live** Claude CLI through `ClaudeCliService.StartAsync`. Gated by `RUN_CLI_INTEGRATION=1`; default `dotnet test` shows them as **explicitly skipped** (xunit `SkippableFact` + `Skip.IfNot`), not silently passed.
   - [`CliWatchdogIntegrationTests.cs`](../backend.Tests/CliWatchdogIntegrationTests.cs): deterministic, zero-quota tests that spawn a `node.exe -e "<script>"` child whose script we control. One script prints a stream-json init frame and stalls forever (the live hang shape); the test asserts the runner stops it cleanly with `RunStopReason.Watchdog` and finalises with `status=stopped`. Another prints five frames and exits, pinning the read-loop's frame-by-frame capture. These run on every default `dotnet test` invocation in well under 2s.

**Caveat (load-bearing).** The original symptom was reproducible only inside the live ASP.NET-hosted backend; it did NOT reproduce in any isolated test - including the `CliSpawnIntegrationTests` matrix that uses the real `ClaudeCliService.StartAsync`. The **`.CMD` hypothesis is not proven**: a `.CMD`-shim invocation with a realistic 8 KB prompt + agent-rules system-prompt-file streams correctly in isolation. The most-likely remaining triggers are (a) some interaction with concurrent `claude.exe` processes already running in the same per-cwd `~/.claude/projects/...` directory, (b) a runtime env-var difference between `dotnet test` and `dotnet run` ASP.NET hosting, or (c) an Anthropic API-side stall that happens to correlate with backend-spawned vs shell-spawned children. We **explicitly leave this open** so the next iteration starts from honest ground.

**Context.** Live runs of `claude.exe` (npm-installed via the `claude.CMD` shim) intermittently emitted their first `system/init` stream-json frame and then went silent until the runner's watchdog killed the process at 124s. Direct shell invocation with the same args worked in 4s. The .NET runner did `Process.Start("claude")` which Windows resolves to `claude.CMD`, which forces a `cmd.exe /c "..."` wrap. We chose the structural cleanup AND the test scaffolding both as a single commit so a future investigator has a baseline regression matrix to extend rather than re-deriving the diagnosis from zero.

The investigation also surfaced a parallel question from the user: "is this a WSL2 vs Windows thing? Should we make WSL2 a requirement?" The answer is no. Node's `process.stdout` block-buffers on pipes on every OS - WSL2 would not fix any buffering hypothesis, and the Windows-specific `.CMD` shim wrapping is solved by addressing it directly. WSL2 as a requirement would force a Linux variant of every CLI (Codex, Gemini, Copilot CLI), lose Windows-native authentication paths (Copilot CLI's Windows TUI), and would not change the underlying mechanics.

**Scope of this change (deliberately narrow).**
- Claude is the only CLI whose `BuildStartInfo` was changed. Codex and Gemini still resolve `GetCliPath()` through `ResolveExecutable()` and accept a `.CMD` path. They were never implicated in the live hang and we have no test evidence that the `.CMD` path harms them. If a similar symptom appears for either, the `ResolveCmdShimToExe` pattern is the obvious next move (Codex's npm shim points at `node.exe + bin/codex.js`, Gemini's at `node.exe + bundle/gemini.js`; the resolver would need to read the shim's `.CMD` text rather than walk a fixed sub-path).
- The base class gained a virtual `SpawnChildAsync` hook + `ChildHandle` abstraction so a future CLI can opt into PTY-based spawning without disturbing the headless pipe path. Today no subclass overrides it.
- The PTY-based spawn override that was briefly added during the investigation was reverted; pseudo-terminals do not co-exist cleanly with `claude -p` (the CLI sees `stdin = TTY` and exits with code 1). The hook is kept for genuinely-interactive CLIs - Copilot's `PtySession` is the existing example.

**Non-goals.**
- Forcing WSL2 / Linux as the runtime. The product targets Windows-native development.
- Running the live `RUN_CLI_INTEGRATION` matrix in default `dotnet test`. They burn real Anthropic quota; CI runs them on a nightly opt-in job, agents run them when investigating spawn issues (`RUN_CLI_INTEGRATION=1 dotnet test --filter CliSpawnIntegrationTests`).
- Mocking the CLI in the live matrix. The whole point of those tests is to drive the live binary so future package updates / Windows patches / CLI version bumps fail loudly. The deterministic coverage of the runner+watchdog chain lives in `CliWatchdogIntegrationTests` instead and uses a fake `node.exe` child.
- Persistent claude-process pooling. Each task run is a fresh process; we accept the spawn cost (~1s) in exchange for clean exit semantics.
- Treating the `ResolveCmdShimToExe` rewrite as a closed root-cause fix. See Caveat. Future work that proves a different trigger may move this rule from "structural cleanup" to "regression workaround" or remove it entirely.

**Reasoning style.** Test what can be tested deterministically, run the live probes opt-in, write down what is unproven so the next reader does not over-trust the fix. The five live probes triangulate observable shapes (`.exe` direct, `.CMD` shim, production code path with realistic prompt, sequential kill+restart). The two deterministic probes pin the runner's stream/stop chain shape using a fake CLI we fully control. Together they catch ~80% of plausible regressions; the missing 20% (live ASP.NET hosting interaction, concurrent-process contention) is what the open caveat is for.

**Implementation pointers.** [`backend/Services/Cli/ClaudeCliService.cs::ResolveCmdShimToExe`](../backend/Services/Cli/ClaudeCliService.cs) (the npm-shim → `.exe` resolver, called from `BuildStartInfo`); [`backend/Services/Cli/ChildHandle.cs`](../backend/Services/Cli/ChildHandle.cs) + [`CliExecutionServiceBase.SpawnChildAsync`](../backend/Services/Cli/CliExecutionServiceBase.cs) (virtual hook for future PTY needs); [`backend.Tests/CliSpawnIntegrationTests.cs`](../backend.Tests/CliSpawnIntegrationTests.cs) (live matrix, `RUN_CLI_INTEGRATION=1` gate); [`backend.Tests/CliWatchdogIntegrationTests.cs`](../backend.Tests/CliWatchdogIntegrationTests.cs) (deterministic fake-CLI tests); [`docs/cli-skills/cli-claude.md`](cli-skills/cli-claude.md) (operator-level "what to check when claude hangs" playbook).

**Status.** Accepted as **mitigation + diagnostics**, not as proven root-cause fix. Open follow-ups: (1) re-run the live matrix on a clean dev backend after a `~/.claude/projects/...` cleanup to test the concurrent-process-contention hypothesis; (2) Codex / Gemini / Copilot smoke probes for parity coverage; (3) extend `CliWatchdogIntegrationTests` to drive `ProjectRunner.TickWatchdog` directly so the state-machine ticks are pinned end-to-end.

---

## ADR-0012 - Existing coding agents are the execution engines, not raw model APIs (2026-05-03)

**Decision.** Agent Software Studio orchestrates existing coding-agent products, primarily CLI or SDK-backed local agents such as Codex, Claude Code, GitHub Copilot, and Gemini, instead of implementing its own API-backed coding-agent runtime. The app owns queues, lifecycle, state movement, protocol generation, review evidence, and cross-CLI fallback. The provider-owned agent owns planning, model/tool loop, editing mechanics, approvals, authentication, model routing, and native IDE or terminal fallback where available.

**Context.** The user wants to keep high-quality coding agents busy and make review easier while using the subscriptions already paid for. Codex and Claude Code are the clearest focus today because they are strong coding agents and have attractive subscription economics. Copilot and Gemini remain supported fallback paths where their CLIs expose enough control. The value of this product is not "another agent loop"; it is the local workbench around existing agents: ordered task queues, deterministic lifecycle boundaries, durable logs, screenshots, protocol summaries, and review handoff.

Recent CLI-integration research reinforces this boundary. OpenAI's Codex clients use a structured App Server protocol over JSON-RPC rather than treating a terminal PTY as the agent API. GitHub's Copilot SDK similarly talks to a Copilot CLI server over JSON-RPC and manages lifecycle from the SDK. VS Code terminal integration is useful for observation and human fallback, but it is not a reliable substitute for a typed agent protocol when the application must classify state, approvals, input requests, and shutdowns.

**Non-goals.**
- Building a custom API-key-billed coding agent loop while the subscription agents remain the primary value path.
- Hiding direct fallback. The user should still be able to drop into Codex, Claude Code, Copilot, Gemini, or a VS Code integration when that is the fastest way to recover or inspect a session.
- Treating PTY automation as the preferred integration layer when a structured protocol, JSONL mode, SDK, or provider session file is available.
- Making one provider permanent. If model economics or provider capabilities shift, the execution-engine boundary can be revisited.

**Reasoning style.** Build the missing workbench, not the agent. Existing coding agents already package a large amount of product engineering: tool approval UX, file editing, prompt/tool policies, auth, session history, model routing, and IDE affordances. Agent Software Studio should spend its complexity budget on the layer those tools do not share: queue utilization, deterministic orchestration, protocol/evidence capture, review ergonomics, and fallback across providers.

**Implementation pointers.** [README.md](../README.md) "Use existing coding agents, not a custom agent runtime"; [ROADMAP.md](../ROADMAP.md) "Product Thesis" and "Hard Boundaries"; CLI contracts in [docs/supported-clis.md](supported-clis.md); per-CLI adapters in [backend/Services/Cli/](../backend/Services/Cli/); deterministic orchestration in [backend/Services/Runner/](../backend/Services/Runner/).

**Status.** Accepted.

---

## ADR-0013 - Typed CliRunEvent adapter contract; phase-aware watchdog; structured channels over PTY (2026-05-03)

**Decision.** The runner ingests a typed event stream from each CLI, not raw byte streams. A new internal contract `CliRunEvent` defines the lifecycle vocabulary every adapter must produce: `RunStarted`, `SessionStarted`, `TurnStarted`, `OutputDelta`, `ToolStarted`, `ToolCompleted`, `Heartbeat`, `TurnCompleted`, `TurnFailed`, `NeedsInput`, `ApprovalRequested`, `ProcessExited`, `Killed`. Each per-CLI driver (Claude / Codex / Copilot / Gemini) is responsible for mapping its native protocol onto this contract:

- **Claude Code:** parse the `--output-format stream-json --verbose` NDJSON frames (system/init, rate_limit_event, assistant, tool_use, tool_result, message_stop) into `CliRunEvent`. The adapter already exists informally in `ClaudeCliService.TransformReadLine` and `OnOutputLine`; the change is to make it produce typed events instead of marker-line strings.
- **Codex:** prefer the **App Server protocol** (JSON-RPC over stdio, schemas at `codex-rs/app-server-protocol`) for new IDE-shape integrations. The current `codex exec --json` JSONL stream is the legacy fallback we keep because it works today; the adapter exposes the same `CliRunEvent` shape from either source so the runner does not care.
- **GitHub Copilot:** prefer the **Copilot SDK** (JSON-RPC against the Copilot CLI server, see `github/copilot-sdk`) where the subscription model permits. The PTY-driven `PtySession` interactive path stays for the human-in-the-loop slash-command probes (`/usage`, `/model`); typed events are produced from screen-scraped state.
- **Gemini:** parse `gemini -p ... -o stream-json` NDJSON the same way as Claude.

The watchdog operates on phase transitions of typed events, not on raw stdout silence. Phases: `Spawning` → `SessionInitializing` → `PromptConsumed` → `TurnInProgress` → `ToolExecuting` → `OutputDelta` → terminal. Each phase has its own silence budget; "no `SessionStarted` within 10 s of spawn" is a different failure mode than "no `OutputDelta` within 60 s of `TurnStarted`" and the orchestrator chat surfaces them differently.

**Context.** A 4-commit investigation (`061b3d9` → `923632e`) made the CLI-spawn boundary safer in concrete ways - direct `claude.exe` invocation, drain-race fix in the read loops, deterministic fake-CLI regression tests, honest ADR-0011 about the unproven root cause. None of those commits found the live "agent silent after init" trigger. Two parallel external code reviews converged on the same diagnosis: the runner is treating CLI output as text-soup when it should be treating it as protocol. A typed adapter layer changes the watchdog's question from "have I seen a stdout byte recently?" to "is the agent in a phase that should be producing events right now?" - and unlike "more PTY", that abstraction has a clear closing condition.

The reference clones at `c:/Projects/agent-taskboard-devspace/cli-source-references/` (cloned during the parallel research thread) confirm the direction:
- `openai-codex/codex-rs/app-server-protocol/`: full JSON-RPC schema for Codex's IDE integration. Event vocabulary maps almost directly onto our `CliRunEvent` shape.
- `github-copilot-sdk/`: SDK protocol with version 3 today; bindings for nodejs, python, dotnet, go, java. The SDK-vs-CLI boundary is exactly the pattern this ADR endorses.
- `anthropics-claude-code/`, `microsoft-vscode-copilot-chat/`, `github-copilot-cli/`: peripheral references; less protocol surface to copy.

**Non-goals.**
- Implementing all four adapters in one commit. The path is incremental: Claude first (because the live hang surfaced there and stream-json is already structured), then Codex (because App Server is the most architecturally sound), then Gemini (parity with Claude), then Copilot (because PTY interaction makes typed events hardest).
- Replacing the existing pipe path before the typed-event path is proven on at least one CLI. ADR-0011's `.CMD → .exe` fix and ADR-0011's drain race fix stay in place as defense-in-depth; this ADR is the next step, not a retroactive deprecation.
- Building Codex's full App Server client. We adopt the protocol's event shape; the transport stays our existing `codex exec --json` for now and migrates when the adapter contract is stable.
- Treating Claude Agent SDK as a path forward. Per ADR-0012, Anthropic's Agent SDK is API-key-based; using it would break the "subscriptions are the budget" boundary. We map the existing `claude -p --output-format stream-json` instead.
- Publishing our `CliRunEvent` shape outside the backend. It is an internal adapter contract, not a public API; renaming events as the contract matures is fine.

**Reasoning style.** Pin the contract first, migrate one CLI to it, prove the watchdog can operate on typed events, then migrate the rest. Each adapter ships with a deterministic fake-CLI regression test (extending the patterns in `CliWatchdogIntegrationTests.cs`) and a live RUN_CLI_INTEGRATION smoke (extending `CliSpawnIntegrationTests.cs`). The phase-aware watchdog tests belong in `WatchdogTests.cs` once the phase enum exists.

**Implementation pointers.** New (not yet written): `backend/Services/Cli/CliRunEvent.cs` (the typed event sum type); `backend/Services/Cli/Adapters/<Cli>EventAdapter.cs` (one per CLI); `backend/Services/Runner/Watchdog.cs` extended with phase logic; `backend.Tests/CliRunEventAdapterTests.cs` (fixture-driven mapping tests, no live process). Existing entry points stay: `CliExecutionServiceBase.StartAsync` keeps its public signature; the read-loop produces typed events via the adapter instead of raw `CliOutputLine`s. Reference clones at `c:/Projects/agent-taskboard-devspace/cli-source-references/`.

**Status.** Accepted as architecture direction. Implementation is staged; the existing pipe-based code path stays in use until each CLI's adapter ships and is proven by tests.

---

## ADR-0014 - Stale-session continuation is a first-class reliability target (2026-05-03)

**Decision.** Continuation after idle, stale, lost, or partially degraded sessions is a product-critical reliability target. Claude Code and Codex are the reference implementations. A resume command accepting a session id is not enough; the resumed run must act on the latest user follow-up, reconcile with job-folder evidence, and produce useful new output, a clear blocker, or a deterministic Recovery hand-off.

**Context.** The user's workflow depends heavily on daily and stale sessions: leave a coding agent alone for an hour or a day, return with a follow-up, and expect the system to continue reliably. Anthropic's [April 23, 2026 Claude Code postmortem](https://www.anthropic.com/engineering/april-23-postmortem) is the canonical external incident. Anthropic traced user-visible quality complaints to harness issues, not model degradation. One issue was specifically stale-session related: a change meant to clear older thinking once after a session had been idle for over an hour kept clearing it on every later turn, making Claude forgetful and repetitive. Their unit tests, end-to-end tests, automated verification, and dogfooding did not catch it because the bug sat at the intersection of context management, prompt caching, extended thinking, and stale sessions.

**Non-goals.**
- Trusting provider session state as the only source of truth. Job-folder evidence remains the recovery substrate: `prompt.md`, `prompt-N.md`, `status.md`, `logs/cli-output.log`, `logs/session-events.jsonl`, and `job.json.sessionChain`.
- Treating all CLIs equally in the next iteration. Claude and Codex define the standard first; Gemini and Copilot follow after the two primary paths are stable.
- Solving stale sessions by always starting fresh. Fresh recovery is the fallback, not the default, because preserving useful provider context is still valuable when it works.
- Building our own API-backed agent loop to avoid provider session bugs. ADR-0012 still stands.

**Reasoning style.** Separate "accepted resume" from "useful continuation". The runner should measure and test both. Accepted resume is a CLI-adapter concern; useful continuation is an orchestrator concern backed by disk evidence and `RunOutcomePolicy`. Live probes are required for provider behavior, but deterministic tests must pin the fallback contract so the same stale id is not retried forever and the user's latest follow-up remains primary during Recovery.

**Implementation pointers.** [ROADMAP.md](../ROADMAP.md) "Stale Session Reliability"; [docs/supported-clis.md](supported-clis.md) "Session model"; [docs/cli-skills/cli-overview.md](cli-skills/cli-overview.md) "Stale-session invariants"; [docs/cli-skills/cli-claude.md](cli-skills/cli-claude.md) "Stale sessions are a harness-quality risk"; [docs/cli-skills/cli-codex.md](cli-skills/cli-codex.md) "Stale Codex sessions"; existing tests in [backend.Tests/TaskRunnerPlanTests.cs](../backend.Tests/TaskRunnerPlanTests.cs), [backend.Tests/RunOutcomePolicyTests.cs](../backend.Tests/RunOutcomePolicyTests.cs), and [backend.Tests/SessionEventsTests.cs](../backend.Tests/SessionEventsTests.cs).

**Status.** Accepted.

---

## ADR-0014 - CLI child stdin is default-deny; pipe only when a payload exists (2026-05-04)

**Decision.** `CliExecutionServiceBase.StartAsync` no longer sets `RedirectStandardInput=true` unconditionally. When the per-CLI subclass returns a non-empty `GetPromptStdinPayload`, we redirect stdin, write the payload, flush, and close - same behaviour as before. When the subclass returns `null` or empty (today's NoOp / probe paths, tomorrow's resume-with-no-followup paths), stdin is **not redirected at all**: the child inherits the parent's already-non-interactive stdin from the ASP.NET host, which is closed-from-the-start, and that closed handle stops the CLI from blocking on a `read(stdin)` call during init.

**Context.** Two empirical findings from research / `cli-orchestration-survey-2026-05.md` and the long-running "agent silent after `system/init`" hang:

1. **claude-code#771 (upstream Anthropic).** The Claude CLI reads stdin during init and blocks on a connected stdin pipe that never delivers EOF in the order Node expects. Python's `subprocess.run(capture_output=True)` works because it sets `stdin=DEVNULL`; Node's documented workaround is `stdio: ['ignore', 'pipe', 'pipe']`. Our .NET equivalent is `RedirectStandardInput=false` (parent's stdin handle is inherited - closed under non-interactive ASP.NET hosting, equivalent to DEVNULL). The race between "child began reading stdin" and ".NET closed the writer end" was reproducible under `dotnet run` + Kestrel but not under `dotnet test`, which is why isolated tests passed while the live backend hung at exactly this seam.
2. **Convergent OSS evidence.** ZENG3LD/gate4agent, aannoo/hcom, awslabs/cli-agent-orchestrator, JeromySt/vscode-copilot-orchestrator, hoangsonww/AI-Agents-Orchestrator, microsoft/copilot-sdk - every cross-CLI orchestrator we surveyed defaults stdin to `'ignore'` / `DEVNULL` and only opens the pipe on a turn that actually has a payload. This is the converged pattern across Rust, TypeScript, Python, and Go orchestrators; it is the contract Anthropic expects and the contract every other CLI tolerates.

**Non-goals.**
- Removing the stdin pipe path entirely. Streaming follow-ups (the bidirectional `--input-format stream-json` mode for Claude continues, App Server / ACP JSON-RPC for Codex / Gemini) need a writable stdin; this ADR pins the *default* behaviour, not the only behaviour.
- Auto-detecting whether the OS will hand the child a NUL stdin. We do not interrogate `Console.IsInputRedirected`; the policy is "unless the subclass asked for stdin redirection, we don't redirect" and the subclass is the source of truth.
- Forcing this on Copilot. Copilot's `CopilotCliService` predates the base class and passes prompts via argv where possible.
- Treating WSL2 as a substitute. See ADR-0015 - claude-code#771 is platform-agnostic; WSL2 does not fix it. The stdin default-deny rule is correct on every platform.

**Reasoning style.** Default-deny matches our `RunPlanner` / `RunOutcomePolicy` aesthetic: the safer behaviour is the default; the unsafe behaviour requires the subclass to opt in. The diagnostic that landed this ADR (`backend.Tests/CliKestrelHostingRepoTests.cs`) reproduces the hang inside `WebApplicationFactory<Program>` so a future regression cannot ship without a red test.

**Implementation pointers.** [`backend/Services/Cli/CliExecutionServiceBase.cs`](../backend/Services/Cli/CliExecutionServiceBase.cs) (`StartAsync` stdin gate); [`backend.Tests/CliKestrelHostingRepoTests.cs`](../backend.Tests/CliKestrelHostingRepoTests.cs) (claude-code#771 hosting repro); the long-form research that grounds this decision lives in [`docs/research/cli-orchestration-survey-2026-05.md`](research/cli-orchestration-survey-2026-05.md) section "R1. Fix stdin-handling per claude-code#771" and the per-repo NOTES.md files under `c:/Projects/agent-taskboard-devspace/cli-source-references/<repo>/`.

**Status.** Accepted.

---

## ADR-0015 - Windows-native runtime; WSL2 is a documented alternative, not a requirement (2026-05-04)

**Decision.** Agent Software Studio's reference platform is Windows-native (.NET on Windows, claude / codex / gemini / copilot CLIs from their official Windows installers). WSL2 is a fully supported alternative for users who prefer it; CI runs on both Windows and Linux runners. We do **not** require WSL2 to use this product, even though some failure modes are easier to reason about under Linux semantics.

**Context.** A long thread of CLI-spawn hangs raised the legitimate question: "should we just require WSL2 and stop fighting Windows?" The research deliverable [`docs/research/wsl2-vs-windows-decision-2026-05.md`](research/wsl2-vs-windows-decision-2026-05.md) (497 lines) examines this seriously. The split that emerged:

- **Genuinely Windows-specific failure modes:** the npm `.CMD` shim wrapping under `cmd.exe /c "..."` (ADR-0011's mitigation), Win32's coarse parent-handle inheritance via `bInheritHandles=TRUE`, ConPTY / winpty quirks for interactive CLIs, locale defaults of CP1252 vs UTF-8. WSL2 eliminates these.
- **Cross-platform failure modes:** Node's stdout block-buffering on pipes (still present on Linux), Anthropic / OpenAI rate limits and stream-json correctness, claude session-file lock contention in `~/.claude/projects/<encoded-cwd>/`, the prompt-trust-store dialog on first run. WSL2 does not eliminate any of these.
- **The dominant suspect (claude-code#771)** is in the cross-platform set. WSL2 does not fix it; the stdin-default-deny rule (ADR-0014) does, on every platform.

**Non-goals.**
- Forcing WSL2. The user's stated environment is Windows-native. Required-WSL2 onboarding adds 3-6 days per new contributor and breaks IDE workflows (Rider's WSL indexing is slow, the `\\wsl$\...` file-system bridge performs poorly for git-heavy workspaces). The benefit (eliminating Windows-specific suspects) is real but small once ADR-0014 is in.
- Pretending Windows-only is fine. We accept the platform-specific code overhead in `ClaudeCliService.ResolveCmdShimToExe`, `CliExecutionServiceBase.ResolveExecutable`, and any future Windows-only handle-curation P/Invoke. This overhead is documented and tested.
- Splitting into separate Windows-only and Linux-only branches. One source tree, platform-conditional code where required.
- Promising perfect feature parity on macOS. macOS works for development but is not the reference platform.

**Reasoning style.** When two execution environments would solve different subsets of a problem, fix the subset they share first. ADR-0014's stdin default-deny is in the shared subset. Only after that lands and a Windows-specific residual remains do we revisit WSL2; the trigger condition is "ADR-0014 plus env hardening shipped, hang still reproduces on Windows but not on Linux / WSL2 CI". Until that condition fires, WSL2 is a documented alternative, not a required runtime.

**Implementation pointers.** [`docs/research/wsl2-vs-windows-decision-2026-05.md`](research/wsl2-vs-windows-decision-2026-05.md) (the long form with empirical evidence per failure mode); [`backend/Services/Cli/ClaudeCliService.cs::ResolveCmdShimToExe`](../backend/Services/Cli/ClaudeCliService.cs) (the existing Windows-specific accommodation, ADR-0011); [`README.md`](../README.md) "Use existing coding agents" (the platform-neutral product framing).

**Status.** Accepted. Re-evaluation trigger: a documented, reproducible hang that survives ADR-0014 + future env hardening on Windows but not on Linux / WSL2 CI.

---

## ADR-0017 - Supervisor as advisory layer above the deterministic orchestrator (2026-05-04)

**Decision.** The supervisor is an *advisory* layer above the orchestrator. It writes typed advisories to a shared per-project log, exposes four pre-emptive primitives (`cancelRun`, `pausePickup`, `forceFail`, `resume`) for the rare emergency, and consults a separate opt-in policy before any action becomes automatic. The orchestrator's deterministic post-run policy (ADR-0002) remains authoritative for routine outcomes.

**Context.** The user identified a missing layer: while the orchestrator decides per-run outcomes after the fact, no continuous external watcher asks "is this run on track right now? is anything stuck? should we intervene?" Building such a layer raises the question of how a higher loop can control a lower one without becoming a parallel orchestrator. The full analysis is in [`docs/research/orchestrator-meta-loop-analysis-2026-05-04.md`](research/orchestrator-meta-loop-analysis-2026-05-04.md).

**Non-goals.**
- A second deterministic orchestrator. The supervisor must not duplicate `RunOutcomePolicy`'s post-run decisions.
- Pre-emptive primitives as the default control path. Cooperative signalling (advisory + the orchestrator's existing tick points) is the default; emergency primitives are reserved for clearly broken behaviour.
- Auto-intervention enabled by default. The auto-intervention policy ships off and stays off until the user explicitly turns it on per instance.
- Inside-the-app guarantees about the supervisor itself surviving every backend failure. That role belongs to Layer 3 (the external system review monitor).

**Reasoning style.** Each layer owns its own state. The orchestrator state machine is single-writer; the supervisor records intent and the runner applies the side effect through existing paths (StopJob, SetMode). Feedback loops are blocked by source-tagging every event and filtering supervisor-sourced events out of supervisor input.

**Implementation pointers.** [`backend/Services/Supervisor/SupervisorContract.cs`](../backend/Services/Supervisor/SupervisorContract.cs) (typed records and ISupervisor surface); [`backend/Services/Supervisor/ProjectObservationService.cs`](../backend/Services/Supervisor/ProjectObservationService.cs) (read-only Observe); [`backend/Services/Supervisor/SupervisorInterventionService.cs`](../backend/Services/Supervisor/SupervisorInterventionService.cs) (the four primitives); [`backend/Services/Supervisor/HardHealthCheckHostedService.cs`](../backend/Services/Supervisor/HardHealthCheckHostedService.cs) (in-process every 10s, advisory-only); [`backend/Services/Supervisor/SoftReasoningHostedService.cs`](../backend/Services/Supervisor/SoftReasoningHostedService.cs) (CLI-driven every 5-10 min, off by default); [`backend/Services/Supervisor/AutoInterventionHostedService.cs`](../backend/Services/Supervisor/AutoInterventionHostedService.cs) (gated, off by default); [`scripts/supervisor/system-review.md`](../scripts/supervisor/system-review.md) (Layer 3 stand-alone monitor).

**Status.** Accepted.

---

## ADR-0018 - Companion App via outbound-only relay (2026-05-04)

**Decision.** The mobile companion surface is reachable from a phone over a public relay that the local processor talks to with outbound-only HTTPS. The processor runs a HostedService that ticks every 10 s, pushing a full snapshot and pulling any queued commands in the same call. The phone is a separate Angular PWA that reads the relay's last snapshot and posts commands. Auth is a shared bearer token over TLS in V1; end-to-end encryption with a paired symmetric key is V2. The HostedService is default-off so a fresh checkout never phones home.

**Context.** The user wants a phone surface for pipeline visibility and for answering NEEDS_INPUT decisions while the local processor sits on a private machine without an inbound port. The constraint is hard: nothing on the local box may listen for inbound connections from the public internet. The chosen shape mirrors how every other phone-to-home tool that respects this constraint works (push-pull through a tiny relay), but it had not been built into this project yet.

**Non-goals.**
- A second SignalR hub on the local box, or any other inbound port. The processor's existing `JobHub` stays a localhost-only socket for the desktop UI.
- Persistent state on the relay. The relay is in-memory; a restart drops the snapshot and the next sync repopulates it within one tick. Lossy is fine for a status mirror.
- Live log streaming from the agent CLI through the relay. Only summarised pipeline + token + quota state goes over the wire. Full log evidence stays in the watched task folders where it already lives.
- Multi-user / multi-tenant. One processor, one shared token, one PWA install.
- End-to-end encryption in V1. The relay sees plaintext until the V2 pairing flow lands; the design doc and roadmap call this out so it cannot be forgotten.
- Mid-task push notifications. The PWA polls while open. VAPID-backed Web Push is V2.

**Reasoning style.** Match the existing supervisor model: optional layer, default-off, single-writer through existing services. The companion command dispatcher must not invent a new path into job state; every command kind translates into an existing in-process service call (`TaskRunnerService.ContinueJob`, `JobMutationService.CreateJob`, `TaskRunnerService.StartJob`). The snapshot builder is a pure function over already-served read surfaces (jobs, runner status, token summary, quota report) so it can be unit-tested without I/O. Outbound-only is the architectural property; everything else is replaceable later.

**Implementation pointers.** [`docs/companion-app-design.md`](companion-app-design.md) (V1 contract with endpoints and DTO shapes); [`backend/Services/Companion/CompanionSyncService.cs`](../backend/Services/Companion/CompanionSyncService.cs) (HostedService tick loop); [`backend/Services/Companion/CompanionSnapshotBuilder.cs`](../backend/Services/Companion/CompanionSnapshotBuilder.cs) (pure snapshot folding); [`backend/Services/Companion/CompanionCommandDispatcher.cs`](../backend/Services/Companion/CompanionCommandDispatcher.cs) (queued command -> existing service); [`companion/relay/Program.cs`](../companion/relay/Program.cs) (relay minimal API).

**Status.** Accepted.

---

## ADR-0019 - Platform owns the commit boundary (2026-05-04)

**Decision.** The runner is the only entity in the system that runs `git commit`. The CLI agent never commits, never pushes, never branches. Commits land deterministically on the `3-progress -> 4-review` transition through `JobTransitionService.MoveAsync`, are gated by a per-project `AutoCommit` setting, use a Haiku-rendered Conventional Commit message from [`prompts/runtime/commit-message.md`](../prompts/runtime/commit-message.md), and stamp the resulting SHA onto `JobInfo.Commit`. Push is the runner's job too; today it is a known gap, deliberately tracked rather than delegated to the CLI.

**Context.** Modern coding CLIs (Claude / Codex / Copilot / Gemini) each ship with their own opinion about whether to commit when they "finish": some will, some won't, none agree on message style, and none align with our state-machine transition. Letting the model commit splits authority over the same working tree between the CLI's own heuristics and the runner's lifecycle policy. The product needs a single author of git history per project so the run timeline, the per-run change set, and `JobInfo.Commit` all line up against the same SHA. The user's framing was explicit: "der Task Prozessor macht den Commit, der Task Prozessor pusht."

**Non-goals.**
- CLI-side commits, even as a fallback when the runner skips. A skipped commit (clean tree, AutoCommit off, run did not reach review) carries meaning. Laundering that signal into a silent CLI write is the failure mode ADR-0002 was written to prevent.
- Orchestrator-side commits via an LLM call ("the run is done, let the orchestrator wrap up by committing"). The orchestrator supervises the commit boundary but does not author git history; supervision speaks via `OrchestratorChatLog` typed entries, not via git.
- Per-CLI commit conventions. The platform produces one shape of commit message regardless of which CLI did the work, so the user reads consistent history.
- Branching, worktrees, PR-shaped review. Per ADR-0001 the product is single-branch per project.
- `git commit --amend` after the fact. Each Progress -> Review transition produces a fresh SHA; amends conflict with the run-timeline's deterministic before/after SHA capture.

**Reasoning style.** Single state-machine writer applied to git history: exactly one path commits, exactly one path pushes, both gated by typed flags, both stamped onto the job for audit. The doctrine is publishable as a marketing claim ("models do work, the platform records work") and operational at the same time, so the public framing and the code path do not drift. Failure modes that look like edge cases (mid-run commit, split-brain message, branch drift, lost SHA stamp) are listed up front in [docs/commit-push-doctrine.md](commit-push-doctrine.md) so a contributor evaluating a new feature can disqualify the wrong patterns without re-deriving them.

**Implementation pointers.** [docs/commit-push-doctrine.md](commit-push-doctrine.md) (full doctrine, marketing layer + internal layer + suggested follow-ups); [`backend/Services/Jobs/JobTransitionService.cs`](../backend/Services/Jobs/JobTransitionService.cs) (commit gate on `3-progress -> 4-review`); [`backend/Services/Runner/RunCompletionPolicy.cs`](../backend/Services/Runner/RunCompletionPolicy.cs) (`ShouldMoveToReview`); [`backend/Services/GitService.cs`](../backend/Services/GitService.cs) (`AutoCommitAsync`, `GenerateCommitMessageAsync`, `Commit`); [`prompts/runtime/commit-message.md`](../prompts/runtime/commit-message.md) (commit-message template); per-CLI confirmation rows in [`docs/cli-skills/cli-claude.md`](cli-skills/cli-claude.md), [`cli-codex.md`](cli-skills/cli-codex.md), [`cli-copilot.md`](cli-skills/cli-copilot.md), [`cli-gemini.md`](cli-skills/cli-gemini.md). Push: not yet implemented; tracked as the first follow-up in `commit-push-doctrine.md`.

**Status.** Accepted.

---

## ADR-0020 - Crash recovery doctrine for in-flight jobs (2026-05-05)

**Decision.** A backend crash mid-job leaves a recoverable state on disk, and the next backend boot resumes cleanly. Three rules carry the doctrine. (1) The runner persists `lastProgressAt` onto `job.json` on every CLI-output flush so the on-disk record reflects which job was alive most recently. (2) Right before the `3-progress -> 4-review` transition, the runner drops a tiny `completion-marker.json` into the job folder; the marker is cleared after a successful move. A marker that survives into the next boot signals "the runner crashed between deciding and moving". (3) On boot, before the first runner tick, `CrashRecoveryService` scans every project: surviving completion markers are completed via the existing `JobTransitionService.MoveAsync`, and uncommitted working-tree changes are committed under a fixed `Crash Recovery <crash-recovery@agent-taskboard>` author and attributed to the most-recently-active `3-progress` job by `lastProgressAt`. Every recovery decision is appended to `logs/backend/recovery.jsonl` and mirrored to the daily backend log.

**Context.** Two silent crashes left agent-produced source changes uncommitted in the dev tree because the runner died after it had told the agent "you're done" but before the `3-progress -> 4-review` transition wired the auto-commit hook. A snapshot commit recovered the work manually. Without doctrine the next failure pattern would have been the same: agent evidence on disk, runner state inconsistent with it, the user reaching for `git status` to find out what was left over. Framing from the user: "wenn das System crasht, soll der Job seinen alten Zustand haben, hoffentlich alles gedumpt, und einfach weitermachen".

**Non-goals.**
- A second authority over job state. Recovery routes through `JobTransitionService.MoveAsync`, never pokes `state` in `job.json` directly. The single-writer state machine (ADR-0001 / ADR-0017) still holds.
- Auto-push during recovery. Push remains the user's gate (AGENTS.md "Stable update policy"); recovery only commits.
- Discarding work. Worst case recovery leaves files for a human to merge. There is no destructive path.
- A second commit author for normal completions. The `crash-recovery` author tag is reserved for boot-time orphan rescue; the regular `3-progress -> 4-review` auto-commit keeps the project's configured author.
- Distributed coordination. Recovery runs synchronously at boot before any runner tick, so the runner sees the recovered state on its first scan and a second crash mid-recovery is itself recoverable on the next boot.
- Per-line `job.json` writes. `lastProgressAt` updates at flush boundaries (post-run, on log writes), not on every emitted token.

**Reasoning style.** Crash recovery is a state-machine concern, not a logging concern: write the marker before the move, clear it after, scan for survivors at boot. The same reasoning applied to ADR-0019 ("platform owns the commit boundary") extends here - the platform also owns the *recovery* commit boundary. The author tag makes recovery commits findable in `git log` without requiring a separate audit table; the JSONL log is the structured side-channel for tools that don't want to grep history.

**Implementation pointers.** [`backend/Services/Runner/CrashRecoveryService.cs`](../backend/Services/Runner/CrashRecoveryService.cs) (boot scan, two-phase recovery, JSONL audit); [`backend/Services/Runner/CompletionMarker.cs`](../backend/Services/Runner/CompletionMarker.cs) (marker schema and lifecycle helpers); [`backend/Services/Runner/ProjectRunner.cs`](../backend/Services/Runner/ProjectRunner.cs) `OnCliFinishedAsync` (writes marker before `_transitions.MoveAsync`, bumps `lastProgressAt` on flush); [`backend/Services/Jobs/JobMutationService.cs`](../backend/Services/Jobs/JobMutationService.cs) `SetJobLastProgressAt`; [`backend/Services/GitService.cs`](../backend/Services/GitService.cs) `CrashRecoveryCommit` / `RepoHasUncommittedChanges` / `ResolveRepoRootForProject`; [`backend/Program.cs`](../backend/Program.cs) (sync boot call before `TaskRunnerService` starts, with crash-recorder fallback); locked by [`backend.Tests/CrashRecoveryServiceTests.cs`](../backend.Tests/CrashRecoveryServiceTests.cs).

**Status.** Accepted.

---

## ADR-0021 - Stable does not restart itself; an external watcher does it at quiet boundaries (2026-05-05)

**Decision.** The stable instance never stops or restarts itself. A separate, sh-only watcher running outside the stable process polls for batch boundaries and, when stable is idle and at least N (default 3) new jobs have arrived in `4-review` since the last restart, delegates to the existing `update-stable.sh` in the parent devspace folder. The watcher lives at [`scripts/supervisor/restart-stable-after-batch.sh`](../scripts/supervisor/restart-stable-after-batch.sh) and its loop wrapper [`scripts/supervisor/run-stable-restart-watcher.sh`](../scripts/supervisor/run-stable-restart-watcher.sh).

**Context.** Stable serves the very source the orchestrated tasks edit. Two silent crashes traced back to a job touching `agent-taskboard-stable/` while stable was running it; ADR-0020 added recovery doctrine but did not address the *trigger* that produced the inconsistency. The user's framing was that stable runs "until the end of all days" through the queue and an external loop is responsible for stopping it, pulling new source, and starting it again at quiet boundaries. Putting that loop inside stable would re-create the original failure mode.

**Non-goals.**
- A daemon or hosted service. The watcher is a sh while-true wrapper, started by the user or a host scheduler. Same convention as Layer 3 system review.
- A second authority over job state. The watcher only reads `<workspace>/projects/<project>/4-review/` directory listings and `GET /api/runner/status`. It never mutates `job.json`, never calls a state-changing endpoint.
- A bypass for the existing update preflight. The watcher always invokes `update-stable.sh`, which gates on a clean worktree and a fast-forward pull. Direct `git pull` from the watcher is forbidden.
- Auto-push. Push remains the user's gate (AGENTS.md "Stable update policy"); the watcher only pulls.
- Restarts driven from inside stable. Any future "I should restart now" signal stable wants to emit goes through this external watcher, not through a self-call.

**Reasoning style.** The runtime that edits its own source must not also be the runtime that decides when to swap that source. Treat the swap boundary as an external concern — the same pattern as supervised processes elsewhere — and pick the boundary by observing both the workload (jobs settling into `4-review`) and the runner's idleness (`activeJobId == null`). When in doubt, skip the tick: a missed restart costs nothing, a mid-job restart costs work.

**Implementation pointers.** [`scripts/supervisor/restart-stable-after-batch.sh`](../scripts/supervisor/restart-stable-after-batch.sh) (single-tick decision logic, JSONL audit at `<workspace>/logs/stable-restarts.jsonl`, snapshot state at `<workspace>/logs/stable-restart-watcher/snapshot.txt`); [`scripts/supervisor/run-stable-restart-watcher.sh`](../scripts/supervisor/run-stable-restart-watcher.sh) (sleep-tick wrapper, default 60 s); [`update-stable.sh`](../../update-stable.sh) (the existing preflight + stop + pull + start chain the watcher reuses unchanged); [`scripts/supervisor/README.md`](../scripts/supervisor/README.md) (operator docs alongside `run-system-review.sh`).

**Status.** Accepted.

---

## ADR-0022 - Orchestrator meta-cycle as the owner of pause-inspect-resume (2026-05-05)

**Decision.** A new per-project loop, the **meta-cycle**, owns the recurring pause-inspect-resume pattern that the supervisor session has been running by hand: pause the runner after N jobs reach `4-review`, inspect a fixed envelope of artefacts (commit-log diff, last-crash marker, supervisor advisories at or above a configurable severity, stuck-in-progress timer, expected per-job artefacts, runner-mode drift), pick exactly one of four actions (`resume`, `update-stable-then-resume`, `queue-fix`, `escalate-to-user`) with a typed reason, and write a structured `MetaCycleReport`. The loop reuses `SupervisorInterventionService.PausePickup` / `Resume` so there is exactly one pause implementation, never moves a job between lanes, never edits source code, and always queues a templated fix-task into `1-preparation` (never `2-ready`) so the human gate is preserved. Off by default; per-project enable in `project-settings.json`. The mockup at [`docs/mockups/orchestrator-meta-cycle/`](mockups/orchestrator-meta-cycle/README.md) is the spec.

**Context.** The supervisor session driving stable was running the same five-step recipe by hand for several iterations: set the runner to `auto-continuous`, watch the queue, pause after N jobs reach `4-review`, inspect artefacts, then either resume (and optionally `update-stable.sh`) or queue a fix. Building this into the supervisor (Layer 2) would conflate two cadences: the supervisor watches a *running* CLI every 10 s; the meta-cycle watches the *batch* shape only at quiet boundaries between jobs. Folding them invites a parallel orchestrator inside the supervisor, which would erode the determinism that ADR-0017 carved out.

**Non-goals.**
- A second pause mechanism. The meta-cycle does not call `SetMode` directly; it routes through the supervisor's existing pre-emptive primitives so the runner stays the single state-machine authority.
- Source-code edits. The meta-cycle queues fix-tasks; a regular CLI run does the editing. The hosted service has no write path that touches anything under `frontend/`, `backend/`, `prompts/`, or `docs/`.
- Auto-promoting fix-tasks to `2-ready`. The default placement is always `1-preparation` so a human reviews. A future per-project flag may opt specific templated topics into `2-ready`; first cut keeps the gate.
- Restarting the backend that hosts the meta-cycle. `update-stable.sh` is invoked as an external sh helper and only ever runs against `stable`; the dev backend that runs the cycle does not enable that action.
- Cross-project rollups. Each project runs its own loop independently. Cross-project review is Layer 3.
- Mid-run intervention. The meta-cycle never cancels a running CLI; that is `CancelRunAsync`'s job.

**Reasoning style.** Cadence picks the layer. High-frequency, low-stakes observation belongs in the supervisor; low-frequency, high-stakes decisions belong above it. Each layer reuses the layer below it for side effects (the meta-cycle calls into the supervisor; the supervisor calls into the runner) so there is exactly one implementation of each effect. Every cycle records its inputs, findings, action, and reason in a structured report so a future contributor (or Layer 3) can reconstruct what happened from disk alone.

**Implementation pointers.** [`docs/mockups/orchestrator-meta-cycle/README.md`](mockups/orchestrator-meta-cycle/README.md) (purpose and boundaries); [`docs/mockups/orchestrator-meta-cycle/taxonomy.md`](mockups/orchestrator-meta-cycle/taxonomy.md) (knobs, checks, action vocabulary, override surface); [`docs/mockups/orchestrator-meta-cycle/ui.html`](mockups/orchestrator-meta-cycle/ui.html) (control panel click-dummy); [`docs/schemas/meta-cycle-report.schema.json`](schemas/meta-cycle-report.schema.json) (report contract); [`backend/Services/Supervisor/MetaCycleHostedService.cs`](../backend/Services/Supervisor/MetaCycleHostedService.cs) and [`backend/Services/Supervisor/MetaCycleRules.cs`](../backend/Services/Supervisor/MetaCycleRules.cs) (per-project ticker and pure check rules, off when `Supervisor:MetaCycleEnabled = false`); [`frontend/src/app/components/project-meta-cycle-section.ts`](../frontend/src/app/components/project-meta-cycle-section.ts) (control panel section on the project detail page).

**Status.** Accepted.

---

## ADR-0023 - JSON-schema-first communication formats and a file-backed in-memory data layer (2026-05-05)

**Decision.** Cross-cutting structured data that flows between layers (disk, backend, frontend, supervisor, companion app) lives behind a JSON Schema, and the backend reads it through a small file-backed in-memory store, not through ad-hoc file handling. Schemas are Draft 2020-12, one concept per file under [`docs/schemas/`](schemas/), named `<concept>.schema.json`, with `$id = https://agent-taskboard.local/schemas/<concept>.schema.json` and camelCase fields to match the backend's Web JSON serialiser. C# records, TypeScript interfaces, and the in-memory store derive from these schemas; the schema is the contract, not the C# type. The data layer is a generic [`InMemoryStore<T>`](../backend/Services/State/InMemoryStore.cs) over JSONL append-only files: load-on-first-access from disk, validate every read and every write against a schema-aligned validator, append under a per-file semaphore, and expose typed access by id, filtered queries, and an append-cursor primitive for incremental consumers (`ReadSince`). Disk is the source of truth; the in-memory projection is a view that can always be rebuilt by re-reading the files. First two concrete consumers are [`SupervisorAdvisoryStore`](../backend/Services/State/SupervisorAdvisoryStore.cs) over `logs/meta/<project>/observations.jsonl` and [`SupervisorInterventionStore`](../backend/Services/State/SupervisorInterventionStore.cs) over `logs/meta/<project>/interventions.jsonl`; [`AutoInterventionHostedService`](../backend/Services/Supervisor/AutoInterventionHostedService.cs) reads new advisories through the store rather than opening the file directly.

**Context.** As more cross-cutting state landed (supervisor advisories, supervisor interventions, meta-cycle reports, agent-message bus, token aggregates, planned audit findings and componentisation metrics), every consumer was opening the same JSONL files with its own `JsonSerializer`, its own cursor bookkeeping, and its own silent skip on malformed lines. The disk format and the in-memory shape were drifting; the companion app and the planned Layer 3 review skills had no contract to read against; new schemas were getting bolted on per consumer without a place that owned validation. The user's framing was explicit: many small JSON schemas, not a database; an in-memory data layer that loads the lot at boot and supports queries / search / aggregation with the same coupling pattern the bus already uses.

**Non-goals.**
- A database engine. No SQLite, no LiteDB, no EF, no embedded server. Every query path is a `List<T>` filter; performance lives in the file layout (one project per directory, JSONL sized for sequential read), not in indexes.
- A single shared schema document. One concept per file is the rule; cross-references live as `$id` URLs, not as `$ref` chains into a giant schema. Aggregate documents are a smell.
- A second authority over what the supervisor / runner does. The store reads and writes structured records; emergency primitives still route through `SupervisorInterventionService` and `TaskRunnerService`. The store does not pause, cancel, or commit.
- Ahead-of-time loading of all schemas at boot. The in-memory projection is per (workspace, project, store) and lazy on first access; eager boot-time loading would make the dev backend pay for every project before the user opens any of them.
- A code-generation step from schema to C# / TypeScript. The schema is the human-readable contract and the test fixture; the records are hand-maintained alongside it. A round-trip test pins the alignment.
- Rejecting unknown fields in legacy lines. The validator checks required fields, known enums, and length constraints; additive optional fields can ship without a schema-version bump. Strict-mode validation runs at append time so new garbage cannot enter; old garbage is skipped on read so a single bad line never breaks the projection.

**Reasoning style.** The schema is the boundary, the store is the projection, the file is the truth. Every cross-cutting record gets a Draft 2020-12 schema before its second consumer lands; the second consumer is the trigger that says "this is no longer one component's private blob". The store hides four operations every consumer was hand-rolling (load, validate, append-with-lock, read-since-cursor) so a new schema needs about ten lines of glue, not a new mini-implementation. Optimistic concurrency for an append-only log is "compare the file's monotonic version after my read"; locking is per-file via `SemaphoreSlim` so one project's writes never serialise across the workspace. The agent-message-bus store (ADR-extension implicit in the bus contract) and this store share the same shape on purpose: there is exactly one persistence pattern in the codebase, with the bus's projection-by-day and this store's projection-by-project as the two configurations.

**Implementation pointers.** [`docs/schemas/README.md`](schemas/README.md) (folder contract, conventions, validation policy); [`docs/schemas/supervisor-advisory.schema.json`](schemas/supervisor-advisory.schema.json), [`docs/schemas/supervisor-intervention.schema.json`](schemas/supervisor-intervention.schema.json), [`docs/schemas/token-aggregate.schema.json`](schemas/token-aggregate.schema.json) (first three concrete schemas); [`docs/schemas/agent-message.schema.json`](schemas/agent-message.schema.json) (extension point shared with the bus, ADR-aligned); [`backend/Services/State/InMemoryStore.cs`](../backend/Services/State/InMemoryStore.cs) (generic file-backed projection: load, validate, append-with-lock, read-since-cursor); [`backend/Services/State/SupervisorAdvisoryStore.cs`](../backend/Services/State/SupervisorAdvisoryStore.cs) and [`backend/Services/State/SupervisorInterventionStore.cs`](../backend/Services/State/SupervisorInterventionStore.cs) (first two consumers); [`backend/Services/State/SupervisorRecordValidator.cs`](../backend/Services/State/SupervisorRecordValidator.cs) (in-code validator mirroring the schema); [`backend/Services/Supervisor/AutoInterventionHostedService.cs`](../backend/Services/Supervisor/AutoInterventionHostedService.cs) (first consumer wired through `SupervisorAdvisoryStore.ReadSince`); locked by [`backend.Tests/InMemoryStoreTests.cs`](../backend.Tests/InMemoryStoreTests.cs) and [`backend.Tests/SchemaRoundTripTests.cs`](../backend.Tests/SchemaRoundTripTests.cs).

**Status.** Accepted.

---

## ADR-0024 - Task Access Layer as the single owner of job storage (2026-05-05)

**Decision.** All reads, lists, mutations, and lane transitions against on-disk job state go through one typed software layer, [`backend/Services/TaskAccess/`](../backend/Services/TaskAccess/), exposed as `ITaskAccess` plus an `ITaskAccessHost` lifecycle. The layer boots once, loads every watched project's lane folders into a typed in-memory index keyed by `(watchPath, lane, jobId)`, watches the filesystem for external changes, and serves cheap reads off the index. Disk stays the source of truth on cold start; the index is a view that can always be rebuilt by re-reading the files. Mutations are narrowly typed (`UpdateField`, `AttachPrompt`, `AppendLogLine`, `Create`); lane transitions have their own typed entry point so the existing single-state-machine authority moves into the layer instead of being duplicated. Every find / list / snapshot call hands out an optimistic-concurrency token (`(version, mtime)`); a later mutation that carries a stale token is rejected with status `Conflict`. Subscribers register per project and receive typed `TaskChange` notifications so the runner, the supervisor, and the SignalR hub stop rescanning. The wire shape is fixed in two Draft 2020-12 schemas: [`docs/schemas/task-find-result.schema.json`](schemas/task-find-result.schema.json) and [`docs/schemas/task-mutation-request.schema.json`](schemas/task-mutation-request.schema.json). Phase 1 ships the contract and the schemas only; the in-memory store, mutations, and consumer migration land in phases 2 through 5 of the queued task `task-access-api-layer-extraction`.

**Context.** Six different services were touching job folders directly: `JobScannerService.FindJob`, `JobMutationService`, `JobStateMachine`, `ProjectRunner`, `ProjectObservationService`, and the Layer 3 review reader. Each one rescanned, each one re-parsed, each one raced every other one. The kanban poll already produced an O(N) disk-rescan regression (see ADR-0023's neighbour and the regression test [`JobsEndpointPerfTests.WithRuntime_Over200Jobs_FinishesWellUnderOneSecond`](../backend.Tests/JobsEndpointPerfTests.cs)). The product roadmap also wants multi-instance and multi-user options later; the only credible way to get there without re-implementing scanning per consumer is to give the codebase one place that owns task storage end-to-end. ADR-0023 already pinned the pattern for cross-cutting structured records (schema first, file-backed in-memory projection); this ADR extends the same pattern to the most touched concept in the system, the job folder itself.

**Non-goals.**
- A database engine. No SQL, no LiteDB, no EF. Files plus an in-memory index, same convention as the supervisor stores and the message-bus store.
- A second state machine. The "one running task per project" rule is not duplicated; it moves into the layer's transition path. The runner still owns it, just from inside the layer instead of from a sibling service.
- Hidden cross-instance synchronisation in phase 1. The typed surface is shaped to support a future HTTP relay (ETag-style optimistic concurrency, schema-fixed wire shapes), but the first cut is in-process. Multi-instance is a phase 5 deliverable, not a phase 1 promise.
- Backwards-compatibility shims for direct-filesystem readers after phase 5. The migration is the migration; no parallel facade survives once the flag flips on.
- Codegen from schema to C# / TypeScript. The schema is the human-readable contract and the test fixture; the records are hand-maintained alongside it (consistent with ADR-0023).
- Aggregate documents or per-job indexes on disk. The on-disk format does not change; this layer only owns the access path.

**Reasoning style.** One software boundary per concept. Job folders are the most-touched concept in the codebase, so they get the same treatment ADR-0023 applied to supervisor advisories and the message bus: schema first, file-backed in-memory projection, one writer pattern, one optimistic-concurrency primitive. Mutations are narrow on purpose: a typed `UpdateField` is harder to misuse than a generic "write whatever you want into job.json", and the typed transition entry point keeps the state machine auditable in one place. Phasing is one commit per phase behind a feature flag (`TaskAccess:Enabled`) so the migration can be reviewed slice by slice; phase 5 flips the default and removes the flag after a healthy stable cycle.

**Implementation pointers.** [`backend/Services/TaskAccess/ITaskAccess.cs`](../backend/Services/TaskAccess/ITaskAccess.cs) (read / list / mutate / transition / subscribe surface); [`backend/Services/TaskAccess/ITaskAccessHost.cs`](../backend/Services/TaskAccess/ITaskAccessHost.cs) (boot / reload / shutdown); [`backend/Services/TaskAccess/TaskAccessRecords.cs`](../backend/Services/TaskAccess/TaskAccessRecords.cs) (request / response / version / change records); [`docs/schemas/task-find-result.schema.json`](schemas/task-find-result.schema.json) and [`docs/schemas/task-mutation-request.schema.json`](schemas/task-mutation-request.schema.json) (wire contracts); [`ROADMAP.md`](../ROADMAP.md) "Task Access Layer" theme; [`backend.Tests/TaskAccessSkeletonTests.cs`](../backend.Tests/TaskAccessSkeletonTests.cs) (phase 1 contract pin); call sites slated for migration in phase 4: [`backend/Services/Jobs/JobScannerService.cs`](../backend/Services/Jobs/JobScannerService.cs), [`backend/Services/Jobs/JobMutationService.cs`](../backend/Services/Jobs/JobMutationService.cs), [`backend/Services/Jobs/JobStateMachine.cs`](../backend/Services/Jobs/JobStateMachine.cs), [`backend/Services/Runner/ProjectRunner.cs`](../backend/Services/Runner/ProjectRunner.cs), [`backend/Services/Supervisor/`](../backend/Services/Supervisor/) observation paths.

**Status.** Accepted.

---

## ADR-0025 - Three-stage review pipeline: progress -> auto-review -> human-review (2026-05-05)

**Decision.** The review step is split into two explicit lanes. The pre-ADR-0025 single `4-review` lane mixed two distinct audiences: orchestrator decisions in flight and tasks waiting on the user. The new lifecycle is `1-preparation -> 2-ready -> 3-progress -> 4-auto-review -> 5-human-review -> 6-completed -> 7-archive`. `4-auto-review` is the orchestrator's machine pass: `ReviewDecisionOrchestrator` reads the lane and either reissues (back to `3-progress`), accepts-as-done (forward to `5-human-review`), or escalates (also forward to `5-human-review` with a `[supervisor]` chat-note). The user always confirms the move from `5-human-review` to `6-completed`; the orchestrator never moves a task directly to `6-completed`. The kanban renders seven columns with distinct visual treatment (machine icon for auto-review, eye icon for human-review). A one-shot ordered migration on backend boot renames the legacy lanes (`4-review -> 4-auto-review`, `5-completed -> 6-completed`, `6-archive -> 7-archive`), rewrites each job.json's `state` field, and is idempotent.

**Context.** The single `4-review` lane was overloaded: jobs waiting on the orchestrator's review-decision tick lived next to jobs waiting on the user's accept-or-reject. The user could not tell at a glance which cards still needed them and which the orchestrator was still chewing on. A previous task (`orchestrator-review-lane-and-bubble-up`) explored Option B - sub-status inside one lane via `OrchestratorVerdict`, rendered as in-column swim-lanes - but the visual subdivision still asked the user to read every card to learn its audience, and it left the data model ambiguous (the lane no longer told you whose turn it was). Option A - a separate lane - makes the audience structural: a job in `4-auto-review` is the orchestrator's responsibility; a job in `5-human-review` is the user's. The user-visible result is a cleaner kanban; the data-model result is that lane membership is the one source of truth for "whose turn it is" so future surfaces (banners, notifications, the meta-cycle) can key off it without re-deriving from a verdict field.

**Non-goals.**
- Intra-project parallelism. Seven lanes are visual; ADR-0001 still applies and only one task per project is in `3-progress` at a time.
- A second state machine. Lane transitions still go through `JobStateMachine.MoveJob` / `JobTransitionService.MoveAsync`; the orchestrator's accept and escalate paths are policy that calls into the same single state-machine entry point.
- Migrating in both directions. The boot-time rename is one-way; the legacy lane folders are removed once empty so the next boot has nothing to do.
- Killing the per-job `OrchestratorVerdict` field on `JobInfo`. The verdict is still useful as a per-card badge (which path the orchestrator took) and stays sourced from the per-project decision journal; it is no longer load-bearing for lane subdivision because the lane itself carries that information.

**Reasoning style.** The lane is the user-visible audience marker. When a single lane has to express "the orchestrator might still touch this" and "you have to touch this", every UI consumer has to re-derive the answer from a sidecar field, every notification rule has to re-implement the same check, and every screenshot of the board hides the answer in card decorations. Splitting the lane resolves that by making the structural question ("whose turn is it?") a structural answer ("look at the column"). The migration is mechanical and idempotent because the lane rename is the entire payload of the change; rewriting `state` in `job.json` follows the rename so the on-disk and in-memory views stay aligned. The per-card visual treatment (machine vs eye icon) is supporting evidence, not the primary signal - the column header carries the meaning.

**Implementation pointers.** [`backend/Models/JobModels.cs`](../backend/Models/JobModels.cs) `JobStates` (constants `AutoReview` / `HumanReview` / renumbered `Completed` and `Archive`, plus `NumberedLegacyMap` for migration); [`backend/Services/Jobs/JobStateMachine.cs`](../backend/Services/Jobs/JobStateMachine.cs) `EnsureStateFoldersAndMigrate` and `MigrateNumberedLane` (boot-time idempotent migration; `LastNumberedLaneMigrationCount` for reporting); [`backend/Services/Runner/ReviewDecisionOrchestrator.cs`](../backend/Services/Runner/ReviewDecisionOrchestrator.cs) (`HandleAcceptAsDone` -> `HumanReview`; `HandleEscalateAsync` / `EscalateNoOpAsync` / `ProcessBlockedAsync` -> `HumanReview` + `[supervisor]` note); [`backend/Services/Runner/ProjectRunner.cs`](../backend/Services/Runner/ProjectRunner.cs) (`AutoReview` is the post-CLI target); [`backend/Endpoints/Jobs/JobCrudEndpoints.cs`](../backend/Endpoints/Jobs/JobCrudEndpoints.cs) (`ValidateTargetState` rejects pre-ADR-0025 lane names with a directed error message; the grouped output exposes both `autoReview` and `humanReview` and keeps a legacy `review` alias); frontend lane catalogue in [`frontend/src/app/app.ts`](../frontend/src/app/app.ts) (`focusGroups`, `laneGroups`); migration test [`backend.Tests/JobStateMachineMigrationTests.cs`](../backend.Tests/JobStateMachineMigrationTests.cs); orchestrator routing test [`backend.Tests/ReviewDecisionOrchestratorRoutingTests.cs`](../backend.Tests/ReviewDecisionOrchestratorRoutingTests.cs); Playwright kanban spec [`frontend/e2e/kanban-seven-lanes.spec.ts`](../frontend/e2e/kanban-seven-lanes.spec.ts).

**Status.** Accepted.

---

## ADR-0026 - Orchestrator preparation lane + autonomy scale (2026-05-05)

**Decision.** A new optional `1a-orchestrator-prep` lane sits between `1-preparation` and `2-ready`; an optional `1b-needs-human-review` lane (hide-when-empty) catches bounces. A per-project `Orchestrator:AutonomyLevel` integer in `0..4` (`manual`, `cautious`, `balanced`, `confident`, `fully-auto`; default `balanced`) governs when the orchestrator-prep loop accepts a task, iterates on it, or bounces it back. The lane structure is **purely additive**: the existing numbered lanes (`1-preparation` ... `7-archive`) are unchanged, and the two new folders use lexicographic sort keys (`1a-`, `1b-`) so they slot between `1-` and `2-` on disk and in the kanban without a rename ripple. A new `OrchestratorPrepHostedService` runs per project, computes a clarity score on each task, and emits one of `accept` / `iterate` / `bounce`; the autonomy level gates the bounce. At level 0 the loop never moves a task forward without a human click. At level 4 it never bounces; an unclear-but-cap-reached task ships to `2-ready` with a `[supervisor]` chat-note. The slider lives in the project header; the next pickup tick honours its new value. The mockup at [`docs/mockups/orchestrator-prep-and-autonomy/`](../mockups/orchestrator-prep-and-autonomy/) is the spec.

**Context.** The kanban after ADR-0025 made it visible whose turn it was on the back end of the pipeline (`4-auto-review` vs `5-human-review`), but the front end had no symmetric structure: tasks moved from `1-preparation` straight to `2-ready` the moment the user dropped them in the queue, and the orchestrator had no chance to inspect a task before the runner picked it up. Two failure modes followed. Tasks with missing acceptance criteria got picked up and the agent invented its own; tasks that contradicted the predecessor in the queue ran to completion before anyone noticed. A second observation was strategic: the orchestrator's primary mandate is "the queue must not stop." Without a configurable knob, every pre-execution check is either always-bounce (queue stalls on borderline tasks) or never-bounce (the orchestrator silently invents scope). Splitting the lane and adding the autonomy scale lets the user pick the trade per project without code change. The numbering rename in the prompt's illustrative plan (`2-orchestrator-prep`, `3-needs-human-review`, ..., `9-archive`) was deliberately rejected; the renumbering ripple through frontend, tests, and existing job folders would have been the dominant cost of the change for no semantic gain. The additive sort-key approach (`1a-`, `1b-`) preserves every existing reference and yields the same kanban order.

**Non-goals.**
- Intra-project parallelism. The prep loop runs at quiet boundaries on the project's pickup loop; it never runs concurrently with the runner. ADR-0001 still holds.
- Bypassing the user. At autonomy 0..1 the queue is allowed to stall on ambiguity; the bounce lane (`1b-needs-human-review`) is the structural answer. The autonomy scale is the only knob that says "decide for me."
- Editing source code in the prep loop. The loop edits `prompt.md` inside the job folder and may write a `prompt-suggested.md`; it never touches code, ADRs, or tests. A bounce writes a typed reason; a fully-auto override writes a `[supervisor]` chat-note. Source edits stay with the runner.
- Replacing the deterministic post-run policy (ADR-0002). The autonomy scale is a pre-execution gate; the post-run path remains the deterministic `RunOutcomePolicy`.
- A second state machine. Lane transitions still go through `JobStateMachine.MoveJob`. `OrchestratorPrepHostedService` is a policy layer that calls into it.

**Reasoning style.** The lane shape is dictated by the audience question that ADR-0025 made explicit: "whose turn is it?". The orchestrator's pre-execution review has the same shape as the post-execution review. `1a-orchestrator-prep` is the orchestrator's machine pass before the runner takes the task; `1b-needs-human-review` is the user's, when the orchestrator hands the question back. The autonomy scale is the load-bearing knob because the cost of bouncing too aggressively (queue stalls) and the cost of bouncing too rarely (orchestrator invents scope) is asymmetric per project: a one-person research project tolerates aggressive autopilot; a shared production project demands a tight bounce. Hard-coding either side is wrong; making the user pick once per project, with a slider that moves at any time, is the minimal-policy answer. The additive lane numbering is a cost-of-change argument: the semantic value of renaming `2-ready` to `4-ready` is zero (nobody reads the number); the cost is a touch of every consumer. Take the cheap path. The bounce reason is typed (`missing-criteria`, `conflicts-prev`, `out-of-scope`, `under-specified`, `iteration-cap`, `external-input`) so the kanban card can render the headline without re-classifying the prompt at draw time. The clarity score is heuristic in the first slice because the heuristics are auditable, the model variant adds token cost, and the bands (`<0.40 / 0.40-0.69 / >=0.70`) are coarse enough to absorb heuristic noise. A fast-model variant is a follow-up slice; the bands and the autonomy gating do not change.

**Implementation pointers.** [`docs/mockups/orchestrator-prep-and-autonomy/`](../mockups/orchestrator-prep-and-autonomy/) (spec); [`backend/Models/JobModels.cs`](../backend/Models/JobModels.cs) `JobStates` (new `OrchestratorPrep` / `NeedsHumanReview` constants; `All[]` extended; legacy maps untouched), `ProjectSettings.AutonomyLevel`; [`backend/Services/Jobs/JobStateMachine.cs`](../backend/Services/Jobs/JobStateMachine.cs) `EnsureStateFoldersAndMigrate` (creates the two new folders idempotently; no rename); [`backend/Services/Supervisor/OrchestratorPrepHostedService.cs`](../backend/Services/Supervisor/OrchestratorPrepHostedService.cs) (per-project tick loop, gated by `Orchestrator:PrepEnabled`, default off; rate-limited by `Orchestrator:PrepCallsPerHour`); [`backend/Services/Runner/OrchestratorPrepRules.cs`](../backend/Services/Runner/OrchestratorPrepRules.cs) (pure-function clarity score and per-level verdict mapping); [`backend/Endpoints/ProjectSettingsEndpoints.cs`](../backend/Endpoints/ProjectSettingsEndpoints.cs) (`GET / PUT /api/projects/{name}/autonomy`); [`backend/Endpoints/Jobs/JobCrudEndpoints.cs`](../backend/Endpoints/Jobs/JobCrudEndpoints.cs) `ValidateTargetState` accepts the new lane names; [`frontend/src/app/app.ts`](../../frontend/src/app/app.ts) (lane catalogue, hide-when-empty for `1b-needs-human-review`); [`frontend/src/app/components/autonomy-slider.ts`](../../frontend/src/app/components/autonomy-slider.ts) (the slider component); migration + rules tests in [`backend.Tests/`](../../backend.Tests/) (`OrchestratorPrepRulesTests.cs`, lane-creation assertion in the existing migration test).

**Status.** Accepted.

---

## ADR-0027 - Continuous decision review for running CLIs (2026-05-06)

**Decision.** While a job is in `3-progress`, the orchestrator continuously scans the active CLI's live output buffer for unresolved interruptive sentinels (`[[TASK_NEEDS_INPUT:...]]`, `[[TASK_BLOCKED:...]]`) and surfaces them as a typed pending-decision record on the project. Detection runs on the existing 5 s pickup tick in `ProjectRunner.TickAsync` (no parallel ticker). The result is exposed at `GET /api/runner/{project}/pending-decisions` and rendered as a prominent live banner on the project view, distinct in colour and shape from the post-run "review-decisions-pending" banner. The user replies through the existing `POST /api/jobs/{jobId}/continue` endpoint with `mode: steer`; the resulting `[user]` log line resolves the sentinel on the next tick and the banner clears on its own. We deliberately reuse the single sentinel grammar in `AgentOutcomeAnalyzer.SentinelRegex` (ADR-0002) instead of introducing a typed side channel between the agent and the runtime.

**Context.** Today the orchestrator only inspects agent output at run end via `AgentOutcomeAnalyzer.Analyze`. An agent that prints `[[TASK_NEEDS_INPUT:...]]` mid-run is invisible until the run finishes; the user only sees it as one chat line in the activity feed. The user's framing made the visibility gap explicit: "Wenn der Task einen Output hat, das sagt: 'Ich brauch jetzt das und das hier ist eine Decision', dann ist das irgendwie so ein Major Punkt. Das muss [...] sehr, sehr gut sichtbar sein und herausstechen." The brief left one open question: typed channel vs. output scanning. The detailed analysis is in [`docs/research/orchestrator-decision-protocol-2026-05.md`](research/orchestrator-decision-protocol-2026-05.md); the short answer is that every supported CLI already prints sentinels to stdout, the runner already keeps the buffer in memory and on disk, the brief explicitly asked us to piggyback on the existing 5 s output-buffer poll, and the detection is structural (sentinel match) rather than heuristic. A typed side channel would split the agent contract into two sub-grammars to keep in sync, force a per-CLI adapter, add a process boundary for emission, and invent a persistence story for backend restarts mid-run, all to deliver the same banner.

**Non-goals.**
- A typed agent-to-runtime channel for mid-run events. The single sentinel grammar from ADR-0002 stays authoritative; the live scanner reuses it.
- Re-implementing the supervisor's hard health-check loop (ADR-0017). Decision detection is structural, not heuristic; it watches for one specific token shape, not for stalls or quality regressions.
- A new write surface. Replies route through the existing `/continue` endpoint; the `[user]` log line is the resolution signal.
- Auto-deciding for the user. The auto-mode `RunOutcomePolicy` path that hands `NEEDS_INPUT` to the per-project orchestrator session (ADR-0008) only fires after the run *ends*; the live banner only fires *during* the run, when the orchestrator has not yet weighed in. They are temporally disjoint.

**Reasoning style.** Same shape as ADR-0002 and ADR-0017: pull deterministic signals out of the prompt-trust path and into a single-grammar parser. A regex on a tail window of the in-memory buffer is the minimum credible implementation; anything more (typed channel, structured event stream) would multiply moving parts without changing what the user sees. The cost-of-change argument also matters: every supported CLI emits sentinels in stdout today, so reuse covers Claude / Codex / Copilot / Gemini in one slice. Distinguishing the live banner from the existing post-run banner is a UI contract (different colour, different shape, with a textarea-and-send affordance) so the human reading the project view never confuses "the agent is still asking, right now" with "the orchestrator owes a decision in the 4-auto-review lane." Reuse of `ReviewDecisionParsing.LineHasFollowUpStream` for resolution detection means the live and post-run banners share one definition of "resolved"; a follow-up that clears one will clear the other.

**Implementation pointers.** [`backend/Services/Runner/PendingDecisionScanner.cs`](../backend/Services/Runner/PendingDecisionScanner.cs) (pure helper, scans a `CliOutputLine` buffer for the latest unresolved interruptive sentinel; reuses `AgentOutcomeAnalyzer.SentinelRegex`); [`backend/Services/Runner/AgentOutcomeAnalyzer.cs::SentinelRegex`](../backend/Services/Runner/AgentOutcomeAnalyzer.cs) (now public so callers share one grammar); [`backend/Services/Runner/ProjectRunner.cs`](../backend/Services/Runner/ProjectRunner.cs) (`TickPendingDecision` invoked from the existing `TickAsync`; `_activePendingDecision` latch; `GetPendingDecisions` surface); [`backend/Services/TaskRunnerService.cs`](../backend/Services/TaskRunnerService.cs) (`GetPendingDecisions(projectName)` proxy); [`backend/Endpoints/RunnerEndpoints.cs`](../backend/Endpoints/RunnerEndpoints.cs) (`GET /api/runner/{project}/pending-decisions` + `RunnerPendingDecisionDto` / `RunnerPendingDecisionsResponse`); [`backend.Tests/PendingDecisionScannerTests.cs`](../backend.Tests/PendingDecisionScannerTests.cs) (regression tests for kind, resolution, latest-wins, tail window); [`frontend/src/app/services/job.service.ts::getRunnerPendingDecisions`](../frontend/src/app/services/job.service.ts) (HTTP client); [`frontend/src/app/components/project-detail.ts`](../frontend/src/app/components/project-detail.ts) (`livePendingDecisions` signal + `.proj-detail__live-banner` styles + `sendLiveDecisionReply` reusing `JobService.continueJob` with `mode: 'steer'`); [`docs/research/orchestrator-decision-protocol-2026-05.md`](research/orchestrator-decision-protocol-2026-05.md) (the long-form scan-vs-channel analysis).

**Status.** Accepted.

## ADR-0028 - Strict-iteration progress-first pickup with loud dead-letter (2026-05-06)

**Decision.** The per-project pickup loop walks **every** `3-progress` folder oldest-first by mtime before considering `2-ready`. A folder qualifies for resume regardless of whether it carries a captured session id or even a `cli-output.log`: the "no log" case means the previous attempt died before the CLI streamed anything, which is the most-restartable case, not the most-skippable. A folder whose autopickup runs have finished without producing a CLI output line for the configured budget (`PickupFailureThreshold`, default 3 consecutive silent attempts; `PickupOutputDeadlineSeconds`, default 60) is dead-lettered into `3a-failed-pickup/<slug>-pickup-failed-<utc-date>/` via `JobStateMachine.MoveFolderToFailedPickup` (single-state-machine authority). Each dead-letter appends one row to `<workspace>/logs/pickup-failures.jsonl` (schema: `docs/schemas/pickup-failure.schema.json`) and drops a `[supervisor] [pickup-failed]` chat-log note on the moved folder. Iteration is exhaustive within a tick: every over-budget folder is dead-lettered before the picker stops on the first under-budget folder, and only an empty `3-progress` lane lets the runner consider `2-ready`.

**Context.** Production observation on 2026-05-06: a runner picked up a fresh `2-ready` job while a `3-progress` folder for the same project still existed. The progress folder lost its `cli-output.log` to a race during a backend restart; the older `GetNextResumableProgressJob` filter required a captured session id to count a progress job as resumable, so the empty folder was skipped. The runner then started a brand-new ready job and the silent progress folder sat for over an hour with no log file and zero progress until a supervisor session manually moved it back to `2-ready`. The user's framing made the priority unambiguous: "Wenn irgendwas in Progress ist, bevorzuge das. Selbst wenn drei, vier, fünf Sachen drin sind, wird einfach aus Progress das nächste gezogen." The earlier code rule "resumable means has a captured session id" was a defensive optimisation that backfired: a folder with no log is the very case where re-running the prompt is safest because no agent context has been spent yet. Pairs with ADR-0001 (one job per project) and the existing loud-not-archived doctrine for boot-sweep verdicts (`StaleProgressArchiver` moves orphans to `3a-failed-pickup`, never silently to `7-archive`); the live pickup loop now follows the same loud doctrine for its own dead-letter path.

**Non-goals.**
- Intra-project parallelism. Iteration through `3-progress` is sequential per project; only one CLI runs per project at a time. The picker stops at the first under-budget folder and returns it; subsequent ticks pick the next one.
- A typed retry-budget surface in `job.json`. The per-slug attempt counter lives in memory only on `ProjectRunner._pickupAttempts`; a backend restart resets it. A restart is itself a recovery boundary, matching the wider runner pattern (`_stuckLoops`, `_consecutiveCaptureFailCount`).
- Auto-fixing the silent run. Dead-letter is a stop-loss, not a repair: the operator inspects `3a-failed-pickup/<slug>-pickup-failed-...` and the `pickup-failures.jsonl` row, decides whether the cause was infrastructure or content, and either deletes the folder or rebuilds the task.
- Time-based deadlines inside the run. The 60 s `PickupOutputDeadlineSeconds` is recorded as the operational meaning of a "silent" attempt; the runtime check happens passively at run-finish (zero captured output lines on an `AutoPickup` against a `3-progress` folder counts as one silent attempt). The watchdog (ADR-0013) still owns mid-run silence detection.

**Reasoning style.** Same shape as ADR-0020 (crash recovery doctrine) and the loud-not-silent rule for `StaleProgressArchiver`: every folder the orchestrator loses sight of has to surface visibly, never silently archive. The strict-iteration rule is the live-loop analogue of the boot-sweep rule. Per-slug attempt counting is the minimum credible circuit-breaker that does not require new disk state; mtime-based oldest-first ordering matches the existing `StaleProgressArchiver.MeasureFolder` shape and is deterministic. The retry budget at 3 was chosen for the same reason as `AutoFailureHaltThreshold` and `CaptureFailHaltThreshold`: a single transient hiccup does not flap the runner; three in a row is structural and warrants the dead-letter move. The threshold is pinned by `PickupLoopStrictIterationTests` so an unintentional change to e.g. 1 fails loudly.

**Implementation pointers.** [`backend/Services/Runner/ProjectRunner.cs`](../backend/Services/Runner/ProjectRunner.cs) (`TryPickProgressJobOrDeadLetter`, `ListProgressFoldersOldestFirst`, `OrderProgressByMtime`, `MeasureProgressFolderMtime`, `RecordPickupAttemptResult`, `DeadLetterUnrecoverableFolder`, `PickupFailureThreshold`, `PickupOutputDeadlineSeconds`); [`backend/Services/Runner/PickupFailureLog.cs`](../backend/Services/Runner/PickupFailureLog.cs) (writer, `PickupFailureRecord`, `PickupAttemptDiagnostic`, `BuildArchiveSlug`, `ProgressPickupCandidate`); [`backend/Services/Jobs/JobStateMachine.cs::MoveFolderToFailedPickup`](../backend/Services/Jobs/JobStateMachine.cs) (single-state-machine entry point for the dead-letter move); [`backend/Models/JobModels.cs::JobStates.FailedPickup`](../backend/Models/JobModels.cs) (`3a-failed-pickup` lane constant); [`docs/schemas/pickup-failure.schema.json`](schemas/pickup-failure.schema.json) (row shape); [`backend.Tests/PickupLoopStrictIterationTests.cs`](../backend.Tests/PickupLoopStrictIterationTests.cs) (mtime ordering, silent-attempt counter, dead-letter move + JSONL row, threshold-pinned constants).

**Status.** Accepted.

---

## ADR-0029 - Boot-sweep pickup failures land in `3a-failed-pickup` with a persistent banner (2026-05-06)

**Decision.** The boot-time `StaleProgressArchiver` sweep no longer routes orphan or empty `3-progress` folders to `7-archive`. Both verdicts now move into `3a-failed-pickup` (the lane introduced by ADR-0028 for the live-pickup dead-letter sibling), each card carrying a `failed-pickup-reason.md` placard with kind (`orphan` / `empty`), last activity, and sweep timestamp. Empty stale folders gain a synthesized minimal `job.json` so the kanban can render the card and the per-job state-field invariant holds. The kanban renders the lane with the kanban-board-design taxonomy's amber treatment (1 px outline, 12 px header dot) and suppresses the lane-collapse button while the lane is non-empty. A persistent amber banner sits at the top of the dashboard whenever any visible (filtered) project has at least one job in `3a-failed-pickup`; clicking the banner scrolls the lane into view and pulses its outline. Banner copy: `<N> jobs failed to pick up. Open the failed-pickup lane.` (singular `job` when N == 1).

**Context.** The kanban-board-design mockup (locked in [`docs/mockups/kanban-board-design/`](mockups/kanban-board-design/)) had specified the optional `failed-pickup` lane (hide-when-empty, amber outline, amber dot, not collapsible while non-empty); ADR-0026 referenced this exact slug (`pickup-failures-loud-not-archived`) as the implementation task for the loud-not-archived contract. ADR-0028 picked up the live-pickup dead-letter side (`ProjectRunner.DeadLetterUnrecoverableFolder` now targets `3a-failed-pickup` via `MoveFolderToFailedPickup`); the boot-sweep side stayed silent until this ADR. Three production traces motivated finishing the contract: the original `arhciv-besser-darzustellen` 31-run loop where a stuck `3-progress` folder vanished into `7-archive` after the resume window expired, the `auto-pickup-cascade` post-mortem where five fresh job folders entered `3-progress` in ten seconds and the boot sweep on the next restart silently archived all of them, and the user's recurring observation that "ich seh's halt nicht" once a folder lands in `7-archive` — the lane returned to one-job-per-project but the orchestrator owed the user a visible signal that the runner had given up. The persistent banner is the cross-board surface that survives a collapsed lane and a project filter; the lane itself stays hide-when-empty so the happy-path board is uncluttered.

**Non-goals.**
- A second state machine. Lane transitions still go through `JobStateMachine.MoveJob`; the new `MoveFolderToFailedPickup` is a thin sibling of `ArchiveFolder` that targets a different state. Single-state-machine authority (per-task constraint) holds.
- Auto-reissue from the failed-pickup lane. ADR-0026 names "failure repair" as part of the orchestrator's primary mandate at autonomy >= 2; that wiring is a separate slice. This ADR is the surface that the reissue path will read from.
- Truly empty `3-progress` folders going to `7-archive` "for noise reduction". Empty folders are now also surfaced loudly with a synthesized `job.json` so the lane card renders. The cost is one extra placeholder per empty stale folder; the benefit is one fewer silent-archive path.
- Migrating the JSONL log on disk. The existing `archiveSlug` field is replaced by `failedPickupSlug` (orphan-recovery) and `destinationSlug` (pickup-failure); the JSONL files are internal logs with no external consumers, so the rename is part of the loud-not-archived contract rather than a compat shim. Old rows in existing files are not rewritten.
- A second amber surface on each card. The lane outline + the header dot + the banner share one colour token (`#f59e0b`); cards inside the lane keep their normal chrome so the eye lands on the lane edge, not on per-card flair.

**Reasoning style.** The user's framing is the rule: pickup failures are noticeable when they happen, not findable in `<workspace>/logs/orphan-recoveries.jsonl` afterwards. That maps cleanly onto a kanban lane plus a persistent banner. The lane is the steady-state surface; the banner is the always-on cross-board surface that survives lane collapse and project filters. Hide-when-empty keeps the happy-path board uncluttered (same rule already applied to `1b-needs-human-review` and `5-human-review`). The `3a-` sort key is the same additive trick ADR-0026 used for `1a-` / `1b-`: ASCII `-` (45) < `a` (97) so `3-progress` sorts before `3a-...`; `3` < `4` so `3a-` sorts before `4-auto-review`. Existing folders, code references, and tests stay valid. Single-state-machine authority means the routing change is a one-line swap inside the existing sweep paths — no parallel state machine, no duplicate file move logic. Writing the placard inside the moved folder rather than only into `<workspace>/logs/` keeps the explanation co-located with the card, so the protocol pane shows it without the operator having to dig through a separate JSONL file. Together with ADR-0028 the doctrine becomes: every code path that loses sight of a `3-progress` folder routes to `3a-failed-pickup`, never to `7-archive`.

**Implementation pointers.** [`backend/Services/Runner/StaleProgressArchiver.cs`](../backend/Services/Runner/StaleProgressArchiver.cs) (`MoveToFailedPickup` replaces the prior `ArchiveOrphan` / `ArchiveEmpty`; writes `failed-pickup-reason.md`; new decision kinds `moved-to-failed-pickup` / `move-to-failed-pickup-failed`); [`backend/Services/Jobs/JobStateMachine.cs::MoveFolderToFailedPickup`](../backend/Services/Jobs/JobStateMachine.cs) (synthesizes a placeholder `job.json` for empty stale folders so the kanban can render the card); [`backend/Endpoints/Jobs/JobCrudEndpoints.cs`](../backend/Endpoints/Jobs/JobCrudEndpoints.cs) (`/api/jobs/grouped` exposes `FailedPickup`); [`docs/schemas/orphan-recovery.schema.json`](schemas/orphan-recovery.schema.json) (renamed kinds + `failedPickupSlug` / `failureKind` fields, `targetState` enum updated); [`backend.Tests/StaleProgressArchiverTests.cs`](../backend.Tests/StaleProgressArchiverTests.cs) (`Sweep_StaleFolderWithoutSentinel_IsMovedToFailedPickupNotSilentlyArchived` / `Sweep_EmptyStaleFolder_IsMovedToFailedPickupNotSilentlyArchived` pin the loud-not-archived contract: nothing lands in `7-archive` on these paths); [`frontend/src/app/models/job.model.ts::GroupedJobs.failedPickup`](../frontend/src/app/models/job.model.ts); [`frontend/src/app/components/job-column.ts`](../frontend/src/app/components/job-column.ts) (`isFailedPickup()` + `.column--failed-pickup` amber outline + `.column__amber-dot` + collapse-button suppression while non-empty); [`frontend/src/app/app.ts`](../frontend/src/app/app.ts) (`failedPickupCount` computed + `scrollToFailedPickupLane` + `.failed-pickup-banner` styles + lane insertion in `focusGroups` / `laneGroups`); [`frontend/e2e/failed-pickup-lane.spec.ts`](../frontend/e2e/failed-pickup-lane.spec.ts) (hide-when-empty + non-empty cases asserting amber outline, dot, collapse-suppressed, banner visible-with-count, click-through scroll).

**Status.** Accepted.

---

## ADR-0030 - Watchdog tuning + loud-failure routing for repeat-killed jobs (2026-05-06)

**Decision.** Three coordinated changes that close the most common live-hang patterns observed across May 2026 plus a UX cleanup:

1. **`SessionInitializing` budget widened from `(30 s, 60 s)` to `(60 s, 120 s)`.** Pattern analysis of 12+ recent live hangs showed `SessionInitializing` was the dominant kill phase, with kills landing at 31-33 s under the old budget. Anthropic's API legitimately takes 30-60 s to handshake under load — particularly when a `rate_limit_event allowed_warning` is in flight — and the old budget read that legitimate slowness as a hung agent. Locked in `PhaseAwareWatchdogTests.SessionInitializing_BudgetIs60And120` so a future tightening cannot ship silently.
2. **`CliRunEvent.Unknown` counts as an activity signal.** Previously, an unclassified frame did not reset the silence clock, so a future stream-json frame variant that the adapter does not yet understand would punish the run with an artificial silence. Counting `Unknown` as activity is the defensive choice; the unknown-sample is captured downstream for diagnosis. Locked in `RunPhaseTransitionsTests.IsActivitySignal_TrueForExpectedKinds`.
3. **Loud-failure routing on N consecutive same-job kills.** When the same job fails `AutoFailureHaltThreshold` runs in a row (today: 3), the runner now moves the job from `3-progress` into `5-human-review` *and* pauses auto-mode. Before this change the job stayed wedged in `3-progress` while auto-mode flipped to `manual` — the user had to manually move the job out before the queue could resume. The chat-note text differentiates the two cases: same-job-repeated says "moved to human review", mixed-offenders says "paused, investigate".

A side-channel heartbeat helper (`backend/Services/Cli/ClaudeSessionHeartbeat.cs`) is shipped but not yet wired into the run lifecycle. Claude writes one JSONL frame per protocol event into `~/.claude/projects/<encoded-cwd>/<session-uuid>.jsonl`; that file does not suffer from Node's stdout pipe block-buffering. Watching its mtime would resurrect the silence clock when the stdout pipe is buffer-stuck. Lifecycle integration (subscribing on `SessionStarted`, disposing on `ProcessExited` / `Killed`) is the first follow-up item — see Open follow-ups below.

A structured per-job tool-call log (`logs/tool-calls.jsonl`) is appended on every `ToolStarted` / `ToolCompleted` event, so a post-mortem of a watchdog kill can answer "what was the last tool the agent started, with what arguments, did the result come back?" without re-grepping the free-text `cli-output.log`.

**Context.** ADR-0011 / ADR-0014 / ADR-0015 closed the spawn-boundary class of failures (`.CMD` shim, `claude-code#771` stdin handling, WSL-vs-Windows). ADR-0013 introduced the typed-event adapter contract and the phase-aware watchdog. The "bang series" of commits between `4759157` and `545ff5f` wired all four CLIs to that contract. After all of that landed, live hangs persisted with a different shape: claude-code emits `system/init` plus 1-2 assistant text frames, then goes silent for 184 s, then is killed by the `OutputDelta` budget — *or* even earlier, killed in `SessionInitializing` at 31-33 s before any frame at all. The session file at `~/.claude/projects/<encoded-cwd>/<session-uuid>.jsonl` continues to grow during these silences (when one exists), which is the empirical evidence for the pipe-buffer hypothesis. The 12+ logs surveyed for this ADR all show kills clustered in `SessionInitializing` (32-33 s) and `OutputDelta`/`TurnInProgress` (61-65 s); none in `ToolExecuting` (180/600 s budget). Anthropic's `rate_limit_event allowed_warning` immediately precedes most hangs, suggesting API-side backpressure under load.

**Non-goals.**
- Building our own Anthropic API client. ADR-0012 still holds: the subscription-installed CLI is the execution engine, not a raw model API.
- Driving claude over a PTY. ADR-0011 caveat: claude `-p` exits with code 1 when it detects `stdin = TTY`. PTY remains usable for Codex / Gemini / Copilot interactive paths but is the dead end for headless claude.
- Increasing the `OutputDelta` budget further. The watcher sample `repository-hygiene-accepted-task-commits` lived 1307 s with multiple sub-budget pauses; increasing the per-pause budget would mask broken runs without helping the legitimate-hang-during-API-stall case. The right knob there is the rate-limit-aware budget bump (next ADR).
- Wiring the heartbeat helper inside this ADR. The helper is shipped as a unit-tested utility; its lifecycle integration in `CliExecutionServiceBase` requires a small per-jobKey state machine that is best done in a dedicated commit with its own integration test. Tracked as a follow-up.

**Reasoning style.** Tune budgets to match the empirical phase distribution, not the theoretical floor. The pattern analysis ([`docs/research/wsl2-vs-windows-decision-2026-05.md`](research/wsl2-vs-windows-decision-2026-05.md) sets the philosophy; the May 6 hang survey extends it) is the load-bearing input — without it the budgets read as guesses. Ship one defensive class change (`Unknown` as activity) so an adapter-coverage gap in any future CLI does not punish the run. Surface repeat-killed jobs loudly because invisible failure is the worst failure mode.

**Implementation pointers.** [`backend/Services/Runner/PhaseAwareWatchdog.cs`](../backend/Services/Runner/PhaseAwareWatchdog.cs) (`SessionInitializing` budget, comments cite this ADR); [`backend/Services/Cli/RunPhaseTransitions.cs`](../backend/Services/Cli/RunPhaseTransitions.cs) (`IsActivitySignal` adds `Unknown`); [`backend/Services/Runner/ProjectRunner.cs`](../backend/Services/Runner/ProjectRunner.cs) (`AppendToolCallLog`, `JobKeyToFolderPath`, `sameJobRepeated` branch in finalization); [`backend/Services/Cli/ClaudeSessionHeartbeat.cs`](../backend/Services/Cli/ClaudeSessionHeartbeat.cs) (helper shipped, wiring deferred); [`backend.Tests/PhaseAwareWatchdogTests.cs`](../backend.Tests/PhaseAwareWatchdogTests.cs::SessionInitializing_BudgetIs60And120); [`backend.Tests/RunPhaseTransitionsTests.cs`](../backend.Tests/RunPhaseTransitionsTests.cs); [`backend.Tests/ClaudeSessionHeartbeatTests.cs`](../backend.Tests/ClaudeSessionHeartbeatTests.cs).

**Open follow-ups.**
- Wire `ClaudeSessionHeartbeat` into `ClaudeCliService.MapLineToRunEvents`: capture `cwd` per `jobKey` at `BuildStartInfo`, instantiate the watcher on `SessionStarted`, dispose on `ProcessExited` / `Killed`. Acceptance: a synthetic test where stdout is buffer-stuck for 90 s but the session file gets appended every 10 s should NOT trigger the watchdog kill.
- Rate-limit-aware budget multiplier: when `RateLimitObserved.status == "allowed_warning"` was seen in the last 60 s, double the `HungSeconds` for `OutputDelta` and `TurnInProgress`. Acceptance: a synthetic test where a rate-limit warning fires followed by 200 s of silence should NOT kill (currently 180 s does).
- Codex parity smoke: confirm the same per-phase budget table applies sensibly to `CodexEventAdapter`-driven runs. The adapter already exists; a `RUN_CLI_INTEGRATION=1` codex spawn under the new budgets should pass without regression.

**Status.** Accepted as the May-2026 hang-survey response. The two follow-ups above are the next iteration; both are tractable in isolation.

---

## ADR-0032 - Contract-bounded agents and loop guards (2026-05-07)

**Decision.** When an LLM is invoked to interpret evidence on behalf of the orchestrator (failure analysis, drift classification, evidence summarization), the call sits between a typed input contract and a typed output contract. The rule engine, not the agent, decides the next action by mapping `(category, confidence)` from the output contract through a deterministic table. Cost and progress are bounded by an explicit two-sided guard: a Pre-Guard refuses the call when budget is exhausted; a Post-Guard refuses the action when the same slug plus category has cycled more than N times. Every loop class is registered in [`docs/loop-inventory.md`](loop-inventory.md) and verified by CI tests.

**Context.** ADR-0002 established that prompt instructions are not load-bearing; sentinels are. ADR-0017 added a supervisor that advises but does not act. The next layer up needed an answer too: when we deliberately invoke an LLM to interpret a failure, and we will because some signals are too rich for sentinels, we need the same discipline. The 2026-05-06 incident proved the cost of the gap: a single broken `claude.exe` quietly drained 22 jobs from the `2-ready` lane through `3a-failed-pickup` in 13 minutes because the runner had no diagnosis layer at all. "No diagnosis" is operationally worse than "diagnosis with hard guardrails", but only if the diagnosis cannot itself become the failure mode.

**Non-goals.**

- Letting the agent's `proposedAction` become the executed action without policy mapping. The agent classifies; the code decides.
- Free-form shell execution as part of self-heal. Self-heal commands are an allow-list keyed by stable string ids; arbitrary commands are rejected.
- An "agent supervisor" that watches another agent for loops. Loop guards are deterministic counters in code, not LLM judgment about whether things are looping.
- Hiding contract artifacts in memory. Both the input and the output contract are written to `<run-folder>/contracts/<step>-input.json` and `<step>-output.json`.
- Using ADR-grade weight for every place a rule engine talks to an LLM. The contract pattern applies wherever the LLM's answer can drive automation; one-shot UI hints (e.g. inline summarize) stay out.
- A recurrent "diagnose the diagnosis" agent. If the first diagnosis hits the Post-Guard, the next step is human review, not another LLM call.

**Reasoning style.** Every agent invocation is treated as a structured RPC: input schema in, output schema out, deterministic dispatch on the output's typed fields. The agent's role is interpretation, not control. When a new loop is opened (any new place where work can re-enter itself), the developer adds an inventory entry, a budget constant, and a breaker test in the same commit. CI enforces the trio. A weekly `[Trait("Category","Weekly")]` architecture test uses an LLM to scan recent diffs against the inventory and proposes new candidate loops; the proposal is itself a contract output, reviewed by a human, never auto-applied.

**Implementation pointers.** [AGENTS.md](../AGENTS.md) "Contract-bounded agents and loop guards"; [docs/agent-contract-pattern.md](agent-contract-pattern.md) (foundational doc with diagram, schemas, worked example); [docs/loop-inventory.md](loop-inventory.md) (registry + per-entry test pointer); first worked example follows ADR-0028 dead-letter at `3a-failed-pickup` (diagnostic agent reads `pickup-failure-context.json`, writes `pickup-failure-diagnosis.json`); marketing positioning in `agent-studio-marketing/06-website-planung/deterministische-guardrails-um-agenten.md`.

**Status.** Accepted.
