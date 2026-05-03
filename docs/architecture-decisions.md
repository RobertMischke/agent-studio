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

**Decision.** Agent Task Processor orchestrates existing coding-agent products, primarily CLI or SDK-backed local agents such as Codex, Claude Code, GitHub Copilot, and Gemini, instead of implementing its own API-backed coding-agent runtime. The app owns queues, lifecycle, state movement, protocol generation, review evidence, and cross-CLI fallback. The provider-owned agent owns planning, model/tool loop, editing mechanics, approvals, authentication, model routing, and native IDE or terminal fallback where available.

**Context.** The user wants to keep high-quality coding agents busy and make review easier while using the subscriptions already paid for. Codex and Claude Code are the clearest focus today because they are strong coding agents and have attractive subscription economics. Copilot and Gemini remain supported fallback paths where their CLIs expose enough control. The value of this product is not "another agent loop"; it is the local workbench around existing agents: ordered task queues, deterministic lifecycle boundaries, durable logs, screenshots, protocol summaries, and review handoff.

Recent CLI-integration research reinforces this boundary. OpenAI's Codex clients use a structured App Server protocol over JSON-RPC rather than treating a terminal PTY as the agent API. GitHub's Copilot SDK similarly talks to a Copilot CLI server over JSON-RPC and manages lifecycle from the SDK. VS Code terminal integration is useful for observation and human fallback, but it is not a reliable substitute for a typed agent protocol when the application must classify state, approvals, input requests, and shutdowns.

**Non-goals.**
- Building a custom API-key-billed coding agent loop while the subscription agents remain the primary value path.
- Hiding direct fallback. The user should still be able to drop into Codex, Claude Code, Copilot, Gemini, or a VS Code integration when that is the fastest way to recover or inspect a session.
- Treating PTY automation as the preferred integration layer when a structured protocol, JSONL mode, SDK, or provider session file is available.
- Making one provider permanent. If model economics or provider capabilities shift, the execution-engine boundary can be revisited.

**Reasoning style.** Build the missing workbench, not the agent. Existing coding agents already package a large amount of product engineering: tool approval UX, file editing, prompt/tool policies, auth, session history, model routing, and IDE affordances. Agent Task Processor should spend its complexity budget on the layer those tools do not share: queue utilization, deterministic orchestration, protocol/evidence capture, review ergonomics, and fallback across providers.

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

**Decision.** Agent Task Processor's reference platform is Windows-native (.NET on Windows, claude / codex / gemini / copilot CLIs from their official Windows installers). WSL2 is a fully supported alternative for users who prefer it; CI runs on both Windows and Linux runners. We do **not** require WSL2 to use this product, even though some failure modes are easier to reason about under Linux semantics.

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
