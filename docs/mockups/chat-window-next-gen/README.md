# Next-Generation Chat Window - Mockup

Design exploration. **A research-backed mockup plus scenario contract.** Goal: make the task chat best-in-class for this product's specific shape: user, task agent, project orchestrator, supervisor, supporting agents, tool calls, tests, commits, and structured decisions in one readable conversation.

This folder is not existing product behavior. It is the target surface for the next Activity Log generation.

## Files

- [ui.html](ui.html) - static visual mockup for the next-generation chat window.
- [scenarios.md](scenarios.md) - typical cases the UI must render well.
- [research.md](research.md) - Stable observations and external product research.
- [evidence/stable-playwright-observations.json](evidence/stable-playwright-observations.json) - Playwright metrics from Stable.
- [evidence/next-gen-chat-mockup-desktop.png](evidence/next-gen-chat-mockup-desktop.png) - rendered desktop screenshot of the proposed mockup.
- [evidence/next-gen-chat-mockup-mobile.png](evidence/next-gen-chat-mockup-mobile.png) - rendered mobile screenshot of the proposed mockup.
- [evidence/tool-heavy-archive-full.png](evidence/tool-heavy-archive-full.png) - Stable screenshot for a tool-heavy archived job.
- [evidence/review-chat-project-switch-full.png](evidence/review-chat-project-switch-full.png) - Stable screenshot for a review job.
- [evidence/analysis-report-review-full.png](evidence/analysis-report-review-full.png) - Stable screenshot for an analysis-report job.

## Recommendation

The current Activity Log is useful evidence but not yet a great conversation surface. It exposes the raw stream too directly. In Stable, tool-heavy jobs already reach this density:

| Stable job | Conversation turns | Tool pills | Tool chips |
|------------|--------------------|------------|------------|
| `chat-read--grep-wiederholunge-mit-weight-darstellen` | 245 | 114 | 148 |
| `chat-wechsel-zwischen-projekten-nicht-gut-moeglich` | 199 | 93 | 124 |
| `project-analysis-reports-surface` | 82 | 39 | 60 |

At that size, the UI has to summarize by default and reveal details on demand. The important next step is not "make the log prettier"; it is to define the conversation grammar.

## Design Goals

1. **Actors are instantly recognizable.** User, task agent, orchestrator, supervisor, supporting agent, and tool runner need different visual anchors, not just differently worded messages.
2. **Runs are the backbone.** A run is one CLI invocation between user inputs. The chat groups messages by run, with run-level status, model, duration, token usage, tests, commits, and outcome.
3. **Tool use is a burst, not a wall.** Consecutive tools collapse into one compact tool-burst card: counts by tool family, failures, changed files, duration, and "expand details".
4. **Orchestrator decisions are first-class.** Reissue, heuristic warning, circuit breaker, supervisor advisory, and user override should look like decisions with reason, evidence, and next action.
5. **The raw trace never disappears.** Conversation mode is the default. Trace mode is one click away and can filter by actor, run, tool family, failure, or artifact.
6. **Screen space is respected.** The default view should fit long jobs by using dense headers, compact actor rails, collapsed tool bursts, and sticky context instead of repeated banners.
7. **Overlays never block core chat controls.** Stable currently shows click interception around conversation/trace controls in some states. The next UI must reserve layout space for run timeline, auto-eval banners, and composer controls.

## Information Architecture

The chat window should have four stacked layers:

| Layer | Purpose | Default density |
|-------|---------|-----------------|
| Session header | Current task, run count, active actor, latest outcome, tokens, tool count, commits/tests | One compact row |
| Run stack | Run cards as the unit of conversation | Collapsed summary with active run expanded |
| Message stream | User, agent, orchestrator, supervisor, tool bursts, artifacts | Conversation mode |
| Composer | Continue, steer, stop, accept, create follow-up | Sticky, mode-aware |

## Actor Model

| Actor | Visual treatment | Typical content |
|-------|------------------|-----------------|
| User | Right-aligned or warm accent, explicit "You" label | Initial prompt, steering, interruption, accept/reject |
| Task agent | Main neutral rail, CLI/model badge | Work narrative, final result, blocked question |
| Orchestrator | Blue decision rail, decision card body | Reissue, heuristic verdict, needs-input answer, circuit breaker |
| Supervisor | Amber or red advisory rail | Stuck warning, health issue, emergency recommendation |
| Supporting agent | Purple/teal secondary rail with skill badge | Security audit, QA check, design council, meta-analysis |
| Tool runner | Compact gray burst card | Read, search, edit, shell, test, browser, screenshot |
| System | Low-emphasis utility row | Parser warning, missing schema, attachment copied |

The actor label should be visible even when the message body is collapsed. Color alone is not enough.

## Tool Burst Model

Tool calls should group by contiguous activity inside one run:

```text
Tools 24 calls | 14 read | 5 search | 3 edit | 2 shell | 1 failed | 2m 11s
Files touched: app.ts, activity-log.ts, activity-log.parser.ts
Tests: npm test failed once, passed on retry
Expand details
```

Expanded detail should show a table with time, tool, target, result, duration, and artifact links. The expanded state is for debugging. The collapsed state is for daily review.

## Orchestrator Decisions

Orchestrator messages should use a typed decision card:

| Field | Example |
|-------|---------|
| Decision | Reissue task follow-up |
| Reason | Agent returned `[[TASK_DONE]]` after 4.6 s without addressing the user follow-up |
| Evidence | Run 3, no commits, no test run, matched sentinel |
| Action | Continue same session with stronger framing |
| Budget | Retry 1/1, orchestrator 6.2k tokens |

This keeps the orchestrator visible without making it look like another model monologue.

## Stable Findings

Playwright against Stable found three concrete issues:

1. The current conversation toggle can be click-blocked by overlapping run-timeline, auto-eval, and activity-panel layers in some job states.
2. Real archived jobs have enough tool activity that per-tool chips dominate the scan path.
3. Actor identity is present in data but weak in the visual hierarchy. The user, task agent, orchestrator, and system messages compete with task-list and protocol UI chrome.

These are design problems, not only bugs. The fix should combine layout reservations, actor grammar, and tool burst summarization.

## External Research Takeaways

The strongest common patterns across Codex, Claude Code, GitHub Copilot Chat, and Gemini Code Assist are:

- Chat is not only prose. It is a control surface with model selection, context, tool access, approvals, and stop/continue actions.
- Agentic modes show plan, tool use, permissions, diffs, and checkpoints separately from the natural-language answer.
- Context is explicit: selected files, terminal output, screenshots, tool responses, MCP servers, and project instructions are shown as attachable or inspectable context.
- Users need approval and rollback affordances around mutating work.
- Tool logs are useful, but the best products separate the readable conversation from the lower-level event log.

See [research.md](research.md) for sources.

## First Implementation Slice

1. Add a frontend-only `ConversationEvent` projection above the existing Activity Log parser. It groups raw entries into actors, runs, tool bursts, decisions, artifacts, and parser warnings.
2. Replace per-tool chips in conversation mode with collapsed `ToolBurstCard` groups. Keep full trace mode for raw entries.
3. Add persistent actor rails and labels for User, Task Agent, Orchestrator, Supervisor, Supporting Agent, Tool Runner, and System.
4. Add typed `DecisionCard` rendering for orchestrator and supervisor events.
5. Fix layout reservations so run timeline, banners, mode controls, stream, and composer cannot overlap or intercept each other.
6. Add Playwright coverage using the Stable evidence cases: tool-heavy archived job, review job with orchestrator output, analysis-report job, failed/empty run, and user-intervention continuation.
7. Add screenshot assertions for desktop and mobile so the next chat never regresses into unreadable dense trace mode.

## Boundaries

- Do not delete the raw log. Conversation mode is a projection.
- Do not make orchestrator decisions look like user messages.
- Do not let tool summarization hide failures. A collapsed failed tool burst must show failure count and severity.
- Do not introduce parallel intra-project work. Supporting agents and checks remain visible sequential runs or explicit supporting evidence.
- Do not rely on color alone for actor identity.
