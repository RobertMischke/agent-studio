# Architecture Decisions

This file is the durable archive for architecture decisions, non-goals, and reasoning styles that emerge from any chat session with this repository. README, ROADMAP, AGENTS, and topic-specific docs (in this folder) are the system where those decisions live; this file is the chronological index that points at them.

Each entry below captures one decision: what was decided, why, what is explicitly **not** going to happen, and how the reasoning was reached. Add a new entry whenever a chat conversation lands on something that future contributors should not have to re-derive. Update or supersede an entry when the underlying decision changes; never delete history silently.

The required shape for each entry:

```markdown
## ADR-NNNN - Short title (YYYY-MM-DD)

**Decision.** One sentence: what was decided.

**Context.** Why the question came up. What was happening when the decision was made.

**Non-goals (do not do).** Bullet list of things this decision rules out, including patterns that look attractive but are off the table.

**Reasoning style.** Short note on the approach used to reach the decision (deterministic vs. heuristic, structural vs. cosmetic, prompt vs. code, etc.). Future agents should be able to apply the same lens to similar questions.

**Implementation pointers.** File paths, function names, doc sections, or commit hashes that carry the decision today.

**Status.** `Accepted` | `Superseded by ADR-MMMM` | `Deprecated`.
```

Numbering is monotonic and never reused. When an ADR is superseded, leave the original entry in place and add a new ADR that supersedes it.

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

**Context.** A session-loss recovery run silently no-op'd a user follow-up and replied "task done" in 4.6 s. The orchestrator accepted that report and moved on. The user surfaced the failure and asked for the steering layer to live in code, not in the prompt.

**Non-goals.**
- Building behavior that relies on the agent obeying soft instructions in a prompt template.
- Hiding orchestrator decisions in backend logs only; the chat must surface them.
- Adding an LLM call to classify outcomes when a hardcoded sentinel can.

**Reasoning style.** Pure-function libraries with their own test matrix per concern (parser → policy → meta channel). Heuristics are allowed only as a fallback and must announce themselves with a meta message so the user sees when the deterministic contract did not match.

**Implementation pointers.** [backend/Services/Runner/AgentOutcomeAnalyzer.cs](../backend/Services/Runner/AgentOutcomeAnalyzer.cs); [backend/Services/Runner/RunOutcomePolicy.cs](../backend/Services/Runner/RunOutcomePolicy.cs); [backend/Services/Runner/OrchestratorChatLog.cs](../backend/Services/Runner/OrchestratorChatLog.cs); [docs/agent-task-contract.md](agent-task-contract.md) "Output Contract"; [README.md](../README.md) "Deterministic orchestration over prompt trust"; commit `cc284cc`.

**Status.** Accepted.

---

## ADR-0003 - Recovery never auto-re-issues; sessionChain is the fallback resume (2026-05-02)

**Decision.** Auto-re-issue fires only on a real Resume-Continue (`plan.EventKind=continue` AND `plan.ResumeFlag`). Recovery + follow-up + no-output posts a meta message and stops. When `sessionName` is empty but the chain still records a non-recovery, non-placeholder UUID, the planner resumes from that chain entry instead of routing back to Recovery.

**Context.** A user-reported recovery loop kept emitting "session lost - recovering from job folder" on every follow-up after a single capture race lost the UUID. The previous re-issue policy then attached "Re-issuing your request as the primary task" to runs that were unrelated new requests, compounding the confusion.

**Non-goals.**
- Stacking automatic retries on a Recovery run; it just burns quota and stacks more recovery on top of broken state.
- Treating a fast no-output Recovery exit as evidence the agent ignored the user. It is more often evidence we lost session capture.
- Reintroducing the empty-string `sessionName` as the only resume signal; the chain is now part of the resume contract.

**Reasoning style.** Symptom in the chat → root cause in the planner + policy. Diagnose two interacting bugs separately, fix each with a small structural change, lock both with planner-matrix tests. Defensive fallback (chain) before chasing the underlying capture race.

**Implementation pointers.** [backend/Services/Runner/RunPlanner.cs](../backend/Services/Runner/RunPlanner.cs) (`LatestRealSessionId`, mode-aware Continue branch); [backend/Services/Runner/RunOutcomePolicy.cs](../backend/Services/Runner/RunOutcomePolicy.cs) (Recovery branch); commit `9969857`.

**Status.** Accepted.

---

## ADR-0005 - Permissive Claude session-id capture + visible capture diagnostics (2026-05-02)

**Decision.** ClaudeCliService now captures the session UUID from any canonical UUID on any stdout line, not only from the `● Session init <uuid>` marker. The first UUID wins; later ones in the same run are ignored. When a CLI run finishes without capturing a UUID, the orchestrator posts a `[capture-fail]` meta message into the chat. When a follow-up routes to Recovery because no session is on record, a `[fallback]` meta message names the mode the user picked and the reason Recovery was chosen. Repeated identical heuristic verdicts inside a Recovery cascade are suppressed after the first.

**Context.** Even after ADR-0003 (sessionChain fallback, Recovery never auto-re-issues), the user observed runs where every follow-up still routed to Recovery and the chat piled up identical "Heuristic verdict: needsinput" notes. Inspection of the activity log showed the agent emitting normal text but no `● Session init <uuid>` marker, so the strict marker regex never fired and the chain stayed empty. The user asked for the Continue path to actually carry the conversation forward and for the loop, when it does happen, to be diagnosable from the chat alone.

**Non-goals.**
- Letting capture failures be silent. The chat must always say so.
- Treating a tool-result UUID later in a run as the session id. We capture only the first UUID we see, which is structurally the session frame.
- Spamming the chat with the same heuristic verdict on every Recovery iteration. One announcement per signature is enough.
- Fixing the root cause of Claude Code's missing marker frame in this commit; the fallback is defensive while we keep the diagnostic data needed to diagnose the underlying CLI behavior.

**Reasoning style.** Defense in depth: keep the strict marker as the intended path, add a permissive UUID match as a safety net, and surface every state transition in the chat so the user can debug the loop without reading backend logs. Suppression by signature, not by time, so the orchestrator stays talkative when the situation actually changes.

**Implementation pointers.** [backend/Services/Cli/ClaudeCliService.cs](../backend/Services/Cli/ClaudeCliService.cs) (`AnyUuidRegex`, hardened `OnOutputLine`); [backend/Services/Runner/ProjectRunner.cs](../backend/Services/Runner/ProjectRunner.cs) (`_lastMetaSignature` suppression, `[capture-fail]` after `OnCliFinished`, `[fallback]` before Recovery starts).

**Status.** Accepted.

---

## ADR-0004 - Four follow-up interaction modes; Extend writes a prompt-N.md timeline (2026-05-02)

**Decision.** `ContinueJobRequest.Mode` is a typed string with four values: `continue`, `steer`, `extend`, `newTask`. Each value selects a prompt frame in `RunPlanner.BuildContinuePrompt`. Extend mode also writes a new `prompt-N.md` (1-based) into the same job folder; the original `prompt.md` is never overwritten. The Task Description pane renders the timeline blog-style.

**Context.** The user's actual workflow is a living chat session: continue, course-correct, extend the task, or open a new sub-task without losing context. Treating every follow-up as a generic next message produced "I'll wait for your request" no-ops on extensions. A single mode value carries the user's intent through the whole run.

**Non-goals.**
- Spawning a new job folder per extension. The user explicitly chose "same folder, blog-style timeline" because new folders multiply state for no gain.
- Editing `prompt.md` in place when the user extends. The original task body stays intact; extensions are append-only.
- Hiding the mode behind a hotkey or hover menu. The pill row is intentionally loud so the user always sees what they are about to send.

**Reasoning style.** UX state is backend state too: the mode is a typed wire field, not a frontend-only flag. Extensions are evidence on disk, not just turns in a chat buffer, so any restart, log inspection, or Haiku summary can read the full timeline.

**Implementation pointers.** [backend/Models/JobModels.cs](../backend/Models/JobModels.cs) (`ContinueModes`, `JobPromptHistoryEntry`); [backend/Services/Runner/RunPlanner.cs](../backend/Services/Runner/RunPlanner.cs) `BuildContinuePrompt`; [backend/Services/TaskRunnerService.cs](../backend/Services/TaskRunnerService.cs) `NextPromptHistoryIndex`; [frontend/src/app/components/job-detail/protocol-pane/protocol-pane.component.ts](../frontend/src/app/components/job-detail/protocol-pane/protocol-pane.component.ts) (`modeOptions`); [frontend/src/app/components/job-detail/prompt-pane/prompt-pane.component.html](../frontend/src/app/components/job-detail/prompt-pane/prompt-pane.component.html) (history block); commits `2b43c7f`, `49fdb57`, `55b8598`.

**Status.** Accepted.
