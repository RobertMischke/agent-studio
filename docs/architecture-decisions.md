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
