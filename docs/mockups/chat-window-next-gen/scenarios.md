# Next-Generation Chat Window Scenarios

The chat window must render these cases clearly before implementation is considered done.

## 1. Fresh Task, No Output Yet

The user opens a ready task that has not run.

Expected UI:

- Empty-state panel says there is no run yet.
- Composer is available with clear start/continue affordance.
- No run timeline or auto-eval layer can overlap the chat-mode controls.

## 2. User Starts The Task

The first event is a user prompt, followed by run creation.

Expected UI:

- User message is visually distinct and labeled `You`.
- Run 1 header appears with CLI, model, start time, and status.
- The agent's first output appears under the task-agent rail.

## 3. Tool-Heavy Agent Work

The agent performs dozens of reads, searches, edits, shell commands, and tests.

Expected UI:

- Conversation mode shows one or more collapsed tool-burst cards.
- Each card shows call count, grouped tool families, failures, duration, touched files, and artifact count.
- Expanding shows the detailed list with time, tool, target, result, duration, and artifact links.
- Trace mode still shows the raw individual entries.

## 4. Tool Failure

A command fails, then the agent retries and succeeds.

Expected UI:

- The collapsed tool burst shows `1 failed` without requiring expansion.
- Expanded details show the failed command, exit status, and retry result.
- If the failure produced a test artifact or screenshot, it is visible as an artifact link.

## 5. Orchestrator Reissue

The task agent reports done too fast or ignores a follow-up. The orchestrator reissues the work.

Expected UI:

- Orchestrator appears as a separate actor.
- Decision card shows decision, reason, evidence, action, retry budget, and token usage.
- The reissued run is connected to the previous run but remains a separate run card.

## 6. Heuristic Warning

No sentinel matched and the outcome policy falls back to heuristic classification.

Expected UI:

- A warning-level orchestrator decision appears.
- It explains that no hard sentinel matched.
- It links to the raw log slice that triggered the heuristic.

## 7. Circuit Breaker

The orchestrator answers repeated `NEEDS_INPUT` turns until budget is exhausted.

Expected UI:

- Loop counter is visible in the run header or decision card.
- The final circuit-breaker event is visually stronger than ordinary orchestrator notes.
- Composer targets the user reply, not another automatic orchestration step.

## 8. Supervisor Advisory

The supervisor notices a stuck run or unsafe pattern.

Expected UI:

- Supervisor appears as a distinct actor, not as task-agent text.
- Advisory includes severity, observed phase, evidence, and recommended action.
- If an emergency primitive is available, it is shown as a deliberate action, not auto-run by default.

## 9. User Intervenes Mid-Run

The user sends steering while a run is active or queued for the next continuation.

Expected UI:

- User intervention is visible as a pinned or high-emphasis turn.
- The target is explicit: current run, next run, orchestrator, or follow-up task.
- The composer mode says whether the message will interrupt, continue, stop, or create a task.

## 10. Supporting Agent Report

A QA, security, design, drift, or meta-analysis agent writes a report.

Expected UI:

- Supporting agent has its own actor rail and skill badge.
- The report summary is visible in the stream.
- Markdown report and structured JSON parse status are inspectable.
- Failed JSON parse shows `Unstructured report` while preserving the raw Markdown.

## 11. Artifact And Screenshot Evidence

The run produces screenshots, test reports, diffs, or generated analysis files.

Expected UI:

- Artifact chip appears in the relevant run or tool burst.
- Screenshot thumbnails are visible when review depends on them.
- Artifact links resolve to durable `results/` paths, not ephemeral Playwright scratch paths.

## 12. Multi-Run Continuation

The task has several runs separated by user follow-ups.

Expected UI:

- Each run has a compact summary.
- The active or latest run is expanded by default.
- Previous runs remain one click away.
- Session header aggregates total runs, commits, tool calls, token usage, and latest outcome.

## 13. Parser Warning

The Activity Log parser cannot classify part of the log.

Expected UI:

- System row shows a parser warning with affected line range.
- The unparsed raw text remains visible in expanded trace.
- The warning does not duplicate the orchestrator's own decision if both refer to the same fact.

## 14. Mobile Narrow View

The user reviews a long job on a narrow viewport.

Expected UI:

- Actor rails compress to labeled initials plus accessible labels.
- Tool bursts stay one-line collapsed by default.
- Composer does not cover the latest message.
- Run header remains usable without horizontal scrolling.

## 15. Bottom Control Bar

The user wants to select model, current chat, agent mode, permissions, start, stop, and configuration without losing chat space.

Expected UI:

- Controls live at the bottom edge beside or above the composer.
- Model selection, chat selection, agent mode, permission level, start, stop, configuration, jobs, debug view, and context chips are interactive in the mockup.
- Changing controls does not inject noisy system messages into the transcript. Important changes appear as subtle events only when needed.

## 16. Continuous Chat With Task Markers

The project chat spans several tasks and continuations.

Expected UI:

- New tasks appear as thin markers, not large cards.
- Hovering or clicking a marker shows task metadata: job id, lane, model, prompt, run, duration, tokens, related commits, and evidence.
- The default transcript still reads as one continuous human conversation.

## 17. Human Layer First, Technical Layer On Demand

Stable chat evidence contains too many technical messages by default.

Expected UI:

- The default text is written for a human reviewer.
- Raw commands, paths, JSON, sentinel matches, prompt payloads, stack traces, and tool outputs are hidden behind details, Trace, Inspector, or Verbose Debug.
- The user can globally toggle the technical layer for debugging.

## 18. Verbose Debug Fullscreen

The user opens a developer debugging view from the compact chat.

Expected UI:

- The view is fullscreen or near-fullscreen and read-only. It does not need a composer.
- It explains the history: actor activity counts, orchestrator activity, supervisor activity, task markers, tool density, duration, token usage, warnings, artifacts, and decision evidence.
- It keeps the dashboard-like diagnostic value from the first mockup while improving the visual hierarchy.
- It can filter or drill down by Agent, Orchestrator, Tools, Warnings, and Tasks.

## 19. Chat-First Reference Layout

The user compares the mockup to modern Codex, Claude Code, and Copilot-style chat surfaces.

Expected UI:

- The default viewport reads as a chat transcript first, not as a dashboard.
- Changed files, tool execution, task transitions, and orchestrator decisions appear as compact collapsible rows.
- Markdown answers are rendered directly in the transcript with readable spacing, code chips, lists, and headings.
- The bottom composer remains visible and contains attachments, permission mode, file context, current chat, model, send, and stop.
- Verbose Debug remains accessible, but it is not the first thing the user sees.

## 20. Existing Application Embedding

The chat design is implemented inside the current Agent Task Processor UI.

Expected UI:

- Task-scoped conversation lives in the existing task-detail Chat tab. It must not require a new global chat window.
- Project-scoped conversation lives in the existing side sheet and can be resized wider for long reading sessions.
- Both surfaces use the same message grammar: user bubbles, agent turns, compact orchestrator rows, collapsed tool rows, subtle task markers, and hidden technical details.
- The side sheet keeps project steering separate from task evidence and task follow-up.
- The task Chat tab keeps prompt, protocol, commits, files, screenshots, and run evidence close to the task.
- The layout remains compatible with the upcoming VS Code-style app chrome: activity bar, tab row, compact panels, status bar, and low padding.

## 21. Light And Dark Theme

The user primarily works in light mode, but the app still supports dark mode.

Expected UI:

- Light theme is treated as a first-class default, not as an afterthought.
- Dark theme uses the same spacing, hierarchy, borders, actor labels, and component behavior.
- Theme changes do not change the conversation grammar or hide status, warning, or failure signals.
- Playwright evidence covers task chat in light, task chat in dark, widened side sheet, and mobile.
