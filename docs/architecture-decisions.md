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
