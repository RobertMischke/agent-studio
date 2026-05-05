# Next-Generation Chat Window - Mockup

Design exploration. **A research-backed mockup plus scenario contract.** Goal: make the task chat best-in-class for this product's specific shape: user, task agent, project orchestrator, supervisor, supporting agents, tool calls, tests, commits, and structured decisions in one readable conversation.

This folder is not existing product behavior. It is the target surface for the next Activity Log generation.

## Files

- [ui.html](ui.html) - interactive v4 visual mockup for the next-generation chat window. This is the current reference.
- [scenarios.md](scenarios.md) - typical cases the UI must render well.
- [research.md](research.md) - Stable observations and external product research.
- [best-practices-comparison.md](best-practices-comparison.md) - focused comparison with VS Code Copilot Chat, Claude Code, Gemini Code Assist, and Codex.
- [visual-audit.md](visual-audit.md) - visual critique of the current Stable chat evidence.
- [evidence/stable-playwright-observations.json](evidence/stable-playwright-observations.json) - Playwright metrics from Stable.
- [evidence/next-gen-chat-mockup-desktop.png](evidence/next-gen-chat-mockup-desktop.png) - rendered desktop screenshot of the proposed mockup.
- [evidence/next-gen-chat-mockup-mobile.png](evidence/next-gen-chat-mockup-mobile.png) - rendered mobile screenshot of the proposed mockup.
- [evidence/next-gen-chat-config-overlay.png](evidence/next-gen-chat-config-overlay.png) - interactive configuration overlay.
- [evidence/next-gen-chat-artifacts-overlay.png](evidence/next-gen-chat-artifacts-overlay.png) - artifact browser overlay.
- [evidence/next-gen-chat-jobs-overlay.png](evidence/next-gen-chat-jobs-overlay.png) - implementation-jobs overlay.
- [evidence/next-gen-chat-tool-details.png](evidence/next-gen-chat-tool-details.png) - expanded tool-burst details.
- [evidence/next-gen-chat-task-marker-popover.png](evidence/next-gen-chat-task-marker-popover.png) - task-marker hover/click metadata.
- [evidence/next-gen-chat-technical-layer.png](evidence/next-gen-chat-technical-layer.png) - global technical-layer toggle.
- [evidence/next-gen-chat-inspector-collapsed.png](evidence/next-gen-chat-inspector-collapsed.png) - compact chat with inspector collapsed.
- [evidence/next-gen-chat-verbose-debug.png](evidence/next-gen-chat-verbose-debug.png) - fullscreen desktop Verbose Debug view.
- [evidence/next-gen-chat-verbose-debug-mobile.png](evidence/next-gen-chat-verbose-debug-mobile.png) - mobile Verbose Debug view.
- [evidence/next-gen-chat-v4-default.png](evidence/next-gen-chat-v4-default.png) - current v4 default chat reference.
- [evidence/next-gen-chat-v4-expanded.png](evidence/next-gen-chat-v4-expanded.png) - current v4 with technical details expanded.
- [evidence/next-gen-chat-v4-debug.png](evidence/next-gen-chat-v4-debug.png) - current v4 Verbose Debug view.
- [evidence/next-gen-chat-v4-mobile.png](evidence/next-gen-chat-v4-mobile.png) - current v4 mobile reference.
- [evidence/tool-heavy-archive-full.png](evidence/tool-heavy-archive-full.png) - Stable screenshot for a tool-heavy archived job.
- [evidence/review-chat-project-switch-full.png](evidence/review-chat-project-switch-full.png) - Stable screenshot for a review job.
- [evidence/analysis-report-review-full.png](evidence/analysis-report-review-full.png) - Stable screenshot for an analysis-report job.

## Recommendation

The current Activity Log is useful evidence but not yet a great conversation surface. It exposes the raw stream too directly, and the first mockup overcorrected toward an operations dashboard. The current v4 direction is a **compact developer chat**: centered transcript, readable markdown, subtle status rows, collapsed file/tool rows, a bottom composer, and a fullscreen verbose debug view. This should feel closer to Codex, Claude Code, and GitHub Copilot Chat than to an operations dashboard.

In Stable, tool-heavy jobs already reach this density:

| Stable job | Conversation turns | Tool pills | Tool chips |
|------------|--------------------|------------|------------|
| `chat-read--grep-wiederholunge-mit-weight-darstellen` | 245 | 114 | 148 |
| `chat-wechsel-zwischen-projekten-nicht-gut-moeglich` | 199 | 93 | 124 |
| `project-analysis-reports-surface` | 82 | 39 | 60 |

At that size, the UI has to summarize by default and reveal details on demand. The important next step is not "make the log prettier"; it is to define the conversation grammar.

The pure chatflow remains the center:

- User and agent messages alternate like a normal chat.
- Orchestrator, supervisor, tool, QA, and artifact events appear as compact inline rows.
- Run metadata, tokens, commits, tests, screenshots, and raw trace filters are available through collapsed rows, modals, or Verbose Debug. They are not a visible side dashboard by default.
- Model selection, chat selection, agent mode, permission level, start, stop, configuration, and context chips live in the bottom composer/control bar.
- New tasks and task continuations appear as subtle timeline markers inside the continuous chat. Hover or click reveals task metadata.
- Large operational cards are expansion states, not the default.
- The original dashboard-style analysis is preserved as `Verbose Debug`: a read-only fullscreen developer view for history, causality, timing, actor counts, tool density, task markers, and orchestrator explanations.

## Design Goals

1. **Actors are instantly recognizable.** User, task agent, orchestrator, supervisor, supporting agent, and tool runner need different visual anchors, not just differently worded messages.
2. **Chatflow wins over dashboarding.** The default view is a compact transcript. Meta information is inline when urgent and in the inspector when supporting.
3. **Runs are thin separators.** A run is one CLI invocation between user inputs, but run boundaries should be slim rows, not large containers around the whole chat.
4. **Tool use is a burst, not a wall.** Consecutive tools collapse into one compact inline tool row: counts by tool family, failures, changed files, duration, and "expand details".
5. **Orchestrator decisions are first-class but terse.** Reissue, heuristic warning, circuit breaker, supervisor advisory, and user override should be one-line decision rows by default. Expanded cards show reason, evidence, action, and budget.
6. **The raw trace never disappears.** Conversation mode is the default. Trace mode is one click away and can filter by actor, run, tool family, failure, or artifact.
7. **The technical layer is opt-in.** Paths, JSON, raw command logs, stack traces, sentinel matches, and prompt payloads are hidden by default and available through details, Trace, Inspector, or Verbose Debug.
8. **Screen space is respected.** The default view should fit long jobs by using dense headers, compact actor rails, collapsed tool bursts, bottom controls, and sticky context instead of repeated banners.
9. **Overlays are interactive.** Config, artifacts, jobs, task-marker popovers, tool details, decision details, inspector collapse, technical layer, start/stop, and Verbose Debug must be clickable in the mockup.
10. **Overlays never block core chat controls.** Stable currently shows click interception around conversation/trace controls in some states. The next UI must reserve layout space for run timeline, auto-eval banners, and composer controls.

## Information Architecture

The chat window should have four layers:

| Layer | Purpose | Default density |
|-------|---------|-----------------|
| Compact header | Current task, state, active run, mode, latest warning | One slim row |
| Message stream | Normal back-and-forth chat with inline events | Conversation mode |
| Trace and debug overlays | Runs, tokens, tools, commits, tests, screenshots, raw trace filters | Opened from compact rows or menu |
| Composer | Continue, steer, stop, accept, create follow-up, slash actions, context chips | Sticky, mode-aware |
| Verbose Debug | Fullscreen read-only history analysis, actor counts, timing, tool density, task markers, explanations | Opened from chat menu |

## Actor Model

| Actor | Visual treatment | Typical content |
|-------|------------------|-----------------|
| User | Right-aligned bubble, warm avatar, explicit `You` label | Initial prompt, steering, interruption, accept/reject |
| Task agent | Left-aligned bubble, CLI/model badge | Work narrative, final result, blocked question |
| Orchestrator | Compact blue inline decision row | Reissue, heuristic verdict, needs-input answer, circuit breaker |
| Supervisor | Compact amber advisory row | Stuck warning, health issue, emergency recommendation |
| Supporting agent | Left-aligned compact report row with skill badge | Security audit, QA check, design council, meta-analysis |
| Tool runner | Inline disclosure row | Read, search, edit, shell, test, browser, screenshot |
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

## Verbose Debug View

The compact chat hides technical noise by default, but developers still need the deeper analysis from the first mockup. `Verbose Debug` is a read-only fullscreen view opened from the chat controls or context menu.

It should answer:

- How often was the task agent active?
- How often did the orchestrator act?
- How often did the supervisor or supporting agents act?
- How long did the whole thread take, and where was time spent?
- Which task markers, runs, tool bursts, warnings, artifacts, and decisions explain the history?
- What is the human-readable explanation of what happened?
- Where can the raw technical trace be opened when needed?

This view is for understanding and debugging, not for composing new chat messages. It can expose dense timelines, actor heatmaps, run duration bands, tool families, token usage, decision evidence, and raw trace links without compromising the compact default chat.

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
4. Add compact decision/advisory rows for orchestrator and supervisor events, with expandable `DecisionCard` details.
5. Add bottom composer controls for current chat, model, agent mode, permission level, start, stop, configuration, jobs, debug view, attachments, and context chips.
6. Add subtle task markers in the continuous chat with hover/click metadata popovers.
7. Add the fullscreen read-only Verbose Debug view.
8. Fix layout reservations so banners, inspector, popovers, mode controls, stream, and composer cannot overlap or intercept each other.
9. Add Playwright coverage using the Stable evidence cases: tool-heavy archived job, review job with orchestrator output, analysis-report job, failed/empty run, and user-intervention continuation.
10. Add screenshot assertions for desktop, mobile, config overlay, artifacts overlay, jobs overlay, tool details, task marker popover, inspector collapsed, technical layer, and Verbose Debug.

## Boundaries

- Do not delete the raw log. Conversation mode is a projection.
- Do not make orchestrator decisions look like user messages.
- Do not let tool summarization hide failures. A collapsed failed tool burst must show failure count and severity.
- Do not introduce parallel intra-project work. Supporting agents and checks remain visible sequential runs or explicit supporting evidence.
- Do not rely on color alone for actor identity.
