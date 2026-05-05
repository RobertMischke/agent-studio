# Next-Generation Chat Window - Mockup

Design exploration. **A research-backed mockup plus scenario contract.** Goal: make the task chat best-in-class for this product's specific shape: user, task agent, project orchestrator, supervisor, supporting agents, tool calls, tests, commits, and structured decisions in one readable conversation.

This folder is not existing product behavior. It is the target surface for the next Activity Log generation.

## Files

- [ui.html](ui.html) - interactive v7 visual mockup for the next-generation chat workbench. This is the current reference.
- [angular-prototype.md](angular-prototype.md) - Angular-hosted clickware prototype, feature flag, evidence, and handoff notes.
- [scenarios.md](scenarios.md) - typical cases the UI must render well.
- [activity-log-edge-cases.md](activity-log-edge-cases.md) - real Activity Log edge-case taxonomy from 136 sampled logs.
- [workbench-layout-research.md](workbench-layout-research.md) - VS Code-inspired layout research for side-by-side chat, Git, result, preview, token, and debug workflows.
- [research.md](research.md) - Stable observations and external product research.
- [best-practices-comparison.md](best-practices-comparison.md) - focused comparison with VS Code Copilot Chat, Claude Code, Gemini Code Assist, and Codex.
- [visual-audit.md](visual-audit.md) - visual critique of the current Stable chat evidence.
- [integration-plan.md](integration-plan.md) - migration plan from today's Activity Log, side sheet, run evidence, token surfaces, and composer controls into the v6 chat grammar.
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
- [evidence/next-gen-chat-v5-task-light.png](evidence/next-gen-chat-v5-task-light.png) - current v5 task-detail Chat tab in light theme.
- [evidence/next-gen-chat-v5-task-dark.png](evidence/next-gen-chat-v5-task-dark.png) - current v5 task-detail Chat tab in dark theme.
- [evidence/next-gen-chat-v5-wide-sidesheet.png](evidence/next-gen-chat-v5-wide-sidesheet.png) - current v5 with widened side-sheet project chat.
- [evidence/next-gen-chat-v5-debug.png](evidence/next-gen-chat-v5-debug.png) - current v5 Verbose Debug from the embedded app context.
- [evidence/next-gen-chat-v5-mobile.png](evidence/next-gen-chat-v5-mobile.png) - current v5 mobile task-chat reference.
- [evidence/next-gen-chat-v6-edge-cases-light.png](evidence/next-gen-chat-v6-edge-cases-light.png) - current v6 light task Chat tab with edge-case scenario rail.
- [evidence/next-gen-chat-v6-wait-loop.png](evidence/next-gen-chat-v6-wait-loop.png) - current v6 wait-loop scenario.
- [evidence/next-gen-chat-v6-image-lightbox.png](evidence/next-gen-chat-v6-image-lightbox.png) - current v6 image evidence lightbox.
- [evidence/next-gen-chat-v6-debug-dark.png](evidence/next-gen-chat-v6-debug-dark.png) - current v6 dark Verbose Debug view.
- [evidence/next-gen-chat-v6-mobile.png](evidence/next-gen-chat-v6-mobile.png) - current v6 mobile task-chat reference.
- [evidence/next-gen-chat-v7-workbench-result.png](evidence/next-gen-chat-v7-workbench-result.png) - current v7 task chat with result summary side pane.
- [evidence/next-gen-chat-v7-workbench-git.png](evidence/next-gen-chat-v7-workbench-git.png) - current v7 task chat with Git changes side pane.
- [evidence/next-gen-chat-v7-workbench-compact.png](evidence/next-gen-chat-v7-workbench-compact.png) - current v7 compact-density workbench state.
- [evidence/next-gen-chat-v7-chat-only.png](evidence/next-gen-chat-v7-chat-only.png) - current v7 chat-only state with the context pane closed.
- [evidence/next-gen-chat-v7-wait-loop.png](evidence/next-gen-chat-v7-wait-loop.png) - current v7 wait-loop scenario inside the workbench.
- [evidence/next-gen-chat-v7-image-lightbox.png](evidence/next-gen-chat-v7-image-lightbox.png) - current v7 image evidence lightbox from the workbench preview.
- [evidence/next-gen-chat-v7-debug-dark.png](evidence/next-gen-chat-v7-debug-dark.png) - current v7 dark Verbose Debug view.
- [evidence/next-gen-chat-v7-mobile.png](evidence/next-gen-chat-v7-mobile.png) - current v7 mobile chat reference.
- [evidence/next-gen-chat-angular-prototype-result.png](evidence/next-gen-chat-angular-prototype-result.png) - Angular prototype Result split.
- [evidence/next-gen-chat-angular-prototype-nav-queue.png](evidence/next-gen-chat-angular-prototype-nav-queue.png) - Angular prototype top chrome with queue popover.
- [evidence/next-gen-chat-angular-prototype-status-tokens.png](evidence/next-gen-chat-angular-prototype-status-tokens.png) - Angular prototype status-bar token heat popover.
- [evidence/next-gen-chat-angular-prototype-status-health.png](evidence/next-gen-chat-angular-prototype-status-health.png) - Angular prototype status-bar system health popover.
- [evidence/next-gen-chat-angular-prototype-status-model.png](evidence/next-gen-chat-angular-prototype-status-model.png) - Angular prototype status-bar CLI and model controls.
- [evidence/next-gen-chat-angular-prototype-all-panes.png](evidence/next-gen-chat-angular-prototype-all-panes.png) - Angular prototype with Result, Git, Preview, and Debug pinned together.
- [evidence/next-gen-chat-angular-prototype-git-editor-split.png](evidence/next-gen-chat-angular-prototype-git-editor-split.png) - Angular prototype Git changes with source editor and vertical splitter.
- [evidence/next-gen-chat-angular-prototype-git-no-chat.png](evidence/next-gen-chat-angular-prototype-git-no-chat.png) - Angular prototype Git/source review with chat closed.
- [evidence/next-gen-chat-angular-prototype-run-popover.png](evidence/next-gen-chat-angular-prototype-run-popover.png) - Angular prototype run marker popover.
- [evidence/next-gen-chat-angular-prototype-rail-guide.png](evidence/next-gen-chat-angular-prototype-rail-guide.png) - Angular prototype Workbench rail guide.
- [evidence/next-gen-chat-angular-prototype-git.png](evidence/next-gen-chat-angular-prototype-git.png) - Angular prototype Git split.
- [evidence/next-gen-chat-angular-prototype-compact.png](evidence/next-gen-chat-angular-prototype-compact.png) - Angular prototype compact density.
- [evidence/next-gen-chat-angular-prototype-lightbox.png](evidence/next-gen-chat-angular-prototype-lightbox.png) - Angular prototype screenshot lightbox.
- [evidence/next-gen-chat-angular-prototype-debug-dark.png](evidence/next-gen-chat-angular-prototype-debug-dark.png) - Angular prototype dark Verbose Debug.
- [evidence/next-gen-chat-angular-prototype-mobile.png](evidence/next-gen-chat-angular-prototype-mobile.png) - Angular prototype mobile collapse.
- [evidence/regression/](evidence/regression/) - durable evidence captured by `next-gen-chat-workbench-regression.spec.ts`. Each image is regenerated on every Playwright run and locks a v7 invariant:
  - `workbench-result.png`, `workbench-git.png`, `workbench-preview.png`, `workbench-debug.png`, `workbench-all-panes.png` - Result, Git, Preview, Debug, and combined pinned states.
  - `workbench-chat-only.png`, `workbench-git-no-chat.png` - Chat-only reclaims space; Git/source review remains usable when chat is closed.
  - `workbench-compact.png`, `workbench-dark.png`, `workbench-light-side-sheet-wide.png`, `workbench-mobile.png` - Compact density, dark theme, light theme with the side sheet wide, mobile collapse.
  - `workbench-splitter-extremes.png` - Keyboard-driven splitter at min and max keeps both panes visible.
  - `edge-tool-burst.png`, `edge-wait-loop.png`, `edge-schema-drift.png`, `edge-decision-showcase.png`, `edge-image-lightbox.png` - Tool burst expansion, wait/circuit decision, schema drift decision, full decision showcase (reissue, heuristic, needs-input, capture-fail, drift, intervention chips), image lightbox.
  - `drilldown-tokens.png`, `drilldown-verbose-debug-trace.png` - Token heat popover and Verbose Debug trace tab stay reachable.
  - `guard-composer-reachable.png` - Click-interception guard: composer remains operable after panes, popovers, and tool burst overlays close.
  - `workbench-side-sheet-restored.png` - Project side sheet toggles independently of the workbench.
- [evidence/tool-heavy-archive-full.png](evidence/tool-heavy-archive-full.png) - Stable screenshot for a tool-heavy archived job.
- [evidence/review-chat-project-switch-full.png](evidence/review-chat-project-switch-full.png) - Stable screenshot for a review job.
- [evidence/analysis-report-review-full.png](evidence/analysis-report-review-full.png) - Stable screenshot for an analysis-report job.

## Recommendation

The current Activity Log is useful evidence but not yet a great conversation surface. It exposes the raw stream too directly, and the first mockup overcorrected toward an operations dashboard. The current v7 direction is a **compact embedded developer chat workbench**: the same message grammar is placed into the existing task-detail Chat tab and the existing project side sheet, and the task chat can keep a small adjacent pane open for result, Git changes, screenshot preview, or debug context. There is no new global chat window. The visual direction is light-first, dark-capable, VS Code-inspired, and compact without turning into an operations dashboard.

The v6 iteration added a log-grounded edge-case taxonomy. A local sweep scanned 136 `cli-output.log` files with 27,634 lines and found recurring cases that must be explicit in the projection contract: tool-heavy bursts, watchdog wait and kill events, needs-input loops, orchestrator reissues, heuristic outcomes, capture-fail/session-rebuild cases, duplicate sentinels, image evidence, test fail/retry/pass runs, token spikes, parser/schema drift, user interventions, and cross-task side-sheet steering.

The v7 iteration adds the workbench layout contract. The task Chat tab keeps the conversation primary while offering explicit split presets: Chat only, Chat plus Result, Chat plus Git, Chat plus Preview, and Chat plus Debug. A compact summary strip surfaces state, tokens, commits, changed files, screenshots, retry warnings, and duration without pushing the transcript down. The adjacent pane is a fast preview and drill-down launcher; full evidence remains in the existing task tabs and Verbose Debug.

The Angular prototype makes the same direction clickable inside the real frontend shell behind `atp.flag.nextGenChatPrototype`. It is intentionally full-screen and self-contained, so production chat remains untouched while the flag is off. The newest iteration is the tall workbench: task metadata, split controls, scenario switches, and run metrics sit in a left task rail or status bar so the transcript and adjacent Git, Result, Preview, Debug, or source-diff pane can use almost the full task height. The prototype now also mirrors more of the existing task-detail product surface: compact task chrome, Complete & Next, a narrower queue list, a richer composer command deck, clickable run markers, and a multi-tab Verbose Debug view. The rail uses inline icons, readable labels in comfortable density, compact icon-only mode, an explanatory guide modal, and an explicit "pin all" action for additive panes. The latest cleanup removes duplicated Chat/Git pane switches from the top chrome: pane visibility belongs in the task rail, the top chrome carries only run summary plus global Sheet and Queue shortcuts, and the bottom bar opens Queue, Token, Health, Evidence, and Model popovers without stealing height from the chat. Chat is optional: Git review can remain open with a changes list on the left and selected source/editor diff on the right, and a real vertical splitter replaces the range slider for adjustable widths. The prototype is lazy/deferred behind the flag to keep the normal app bundle lighter. See [angular-prototype.md](angular-prototype.md).

In Stable, tool-heavy jobs already reach this density:

| Stable job | Conversation turns | Tool pills | Tool chips |
|------------|--------------------|------------|------------|
| `chat-read--grep-wiederholunge-mit-weight-darstellen` | 245 | 114 | 148 |
| `chat-wechsel-zwischen-projekten-nicht-gut-moeglich` | 199 | 93 | 124 |
| `project-analysis-reports-surface` | 82 | 39 | 60 |

At that size, the UI has to summarize by default and reveal details on demand. The important next step is not "make the log prettier"; it is to define the conversation grammar.

The pure chatflow remains the center, but it has two homes in the existing app:

- Task-level chat lives in the task detail Chat tab, next to Prompt, Protocol, Files, Commits, and Screenshots.
- Project-level chat lives in the resizable side sheet, where cross-task steering and project context already belong.
- Task-level chat can open additive workbench panes for result, Git changes, screenshots, or debug summaries while the transcript remains visible.
- Chat is optional. Each workbench pane is a toggle, and Git/source review must still work when chat is closed.
- Sources are part of Git changes in this workbench. They are not a standalone source-browser mode.
- Integration must start from the existing implementation, not from a pasted mockup. Preserve the Activity Log parser, Trace mode, run timeline, auto-eval banner, task composer, project side sheet, CLI Usage sheet, Status Bar quota, Workspace Token Timeline, and project token summaries unless an equivalent replacement is already implemented and tested.
- The chat renderer should sit behind a separate `Frontend:NextGenChat` flag so it can land independently of the `Frontend:VsCodeLayout` shell flag.
- User and agent messages alternate like a normal chat in both places.
- Orchestrator, supervisor, tool, QA, and artifact events appear as compact inline rows.
- Run metadata, tokens, commits, tests, screenshots, and raw trace filters are available through collapsed rows, modals, or Verbose Debug. They are not a visible side dashboard by default.
- Model selection, chat selection, agent mode, permission level, start, stop, configuration, and context chips live in the relevant composer/control bar.
- New tasks and task continuations appear as subtle timeline markers inside the continuous chat. Hover or click reveals task metadata.
- Large operational cards are expansion states, not the default.
- The original dashboard-style analysis is preserved as `Verbose Debug`: a read-only fullscreen developer view for history, causality, timing, actor counts, tool density, task markers, and orchestrator explanations.
- Light theme is a first-class default. Dark theme must use the same spacing, hierarchy, and component grammar, not a separate design.

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
11. **Side-by-side review is a first-class task workflow.** The user can keep chat open while inspecting result summary, Git changes, screenshot evidence, token risk, or debug context. The panes are additive toggles and can all be visible when the user needs a full review surface. Pane controls live in the task rail, not duplicated across the top chrome. This is still not a full docking window manager.
12. **Chat can get out of the way.** Review mode must allow closing chat so Git changes and the selected source editor/diff can use the workbench. Panel visibility and width are user-controlled toggles, not fixed layout assumptions.

## Information Architecture

The chat window should have four layers:

| Layer | Purpose | Default density |
|-------|---------|-----------------|
| Existing app chrome | Activity bar, tabs, status bar, task detail, resizable side sheet | Always visible |
| Task Chat tab | Task-scoped conversation, run evidence, follow-up, artifacts | Active task detail surface |
| Side sheet chat | Project-scoped steering, current project context, cross-task notes | Resizable project surface |
| Trace and debug overlays | Runs, tokens, tools, commits, tests, screenshots, raw trace filters | Opened from compact rows or menu |
| Composer | Continue, steer, stop, accept, create follow-up, slash actions, context chips | Surface-specific |
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

1. Add `Frontend:NextGenChat` and the integration bridge described in [integration-plan.md](integration-plan.md). Inventory the current hosts and token surfaces before changing the visible UI.
2. Add a frontend-only `ConversationEvent` projection above the existing Activity Log parser. It groups raw entries into actors, runs, tool bursts, decisions, artifacts, and parser warnings.
3. Render the projection inside the existing Protocol pane Activity tab behind the flag. Keep Trace mode, run timeline access, auto-eval banner, and composer behavior intact.
4. Replace per-tool chips in conversation mode with collapsed `ToolBurstCard` groups. Keep full trace mode for raw entries.
5. Add persistent actor rails and labels for User, Task Agent, Orchestrator, Supervisor, Supporting Agent, Tool Runner, and System.
6. Add compact decision/advisory rows for orchestrator and supervisor events, with expandable `DecisionCard` details.
7. Adapt the existing project side sheet to the shared message grammar without removing project picker, task tab, roadmap intake, attachments, or make-task behavior.
8. Add bottom composer controls for current chat, model, agent mode, permission level, start, stop, configuration, jobs, debug view, attachments, and context chips.
9. Add subtle task markers in the continuous chat with hover/click metadata popovers.
10. Add the fullscreen read-only Verbose Debug view.
11. Fix layout reservations so banners, inspector, popovers, mode controls, stream, and composer cannot overlap or intercept each other.
12. Add Playwright coverage using the Stable evidence cases: tool-heavy archived job, review job with orchestrator output, analysis-report job, failed/empty run, and user-intervention continuation.
13. Add screenshot assertions for desktop, mobile, light theme, dark theme, config overlay, artifacts overlay, jobs overlay, tool details, task marker popover, inspector collapsed, technical layer, side sheet wide mode, edge-case scenario rail, image lightbox, wait-loop state, and Verbose Debug.

## Boundaries

- Do not delete the raw log. Conversation mode is a projection.
- Do not make orchestrator decisions look like user messages.
- Do not let tool summarization hide failures. A collapsed failed tool burst must show failure count and severity.
- Do not introduce parallel intra-project work. Supporting agents and checks remain visible sequential runs or explicit supporting evidence.
- Do not rely on color alone for actor identity.
