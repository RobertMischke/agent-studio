# Embedded Chat Integration Plan

This plan explains how the chat mockup should move into the existing app without losing the current product surface. The goal is not to paste the mockup over the app. The goal is to migrate the current Activity Log, project side sheet, run evidence, token indicators, task controls, and the v7 task-chat workbench split into one shared chat grammar.

## Current Sources To Preserve

The existing implementation already has valuable pieces:

| Current surface | Source | Must preserve |
|-----------------|--------|---------------|
| Task Activity tab | `frontend/src/app/components/activity-log-view.ts` inside `protocol-pane` | Conversation and Trace modes, raw log access, live status, tool grouping, copy visible output |
| Task run timeline | `frontend/src/app/components/job-detail/protocol-pane/run-timeline.component.ts` | Run boundaries, run filters, commits, file changes, active run state |
| Task composer | `frontend/src/app/components/job-detail/protocol-pane/protocol-pane.component.html` | Continue, Steer, Extend, New task, send, stop, running state |
| Project side sheet | `frontend/src/app/components/orchestrator-side-sheet/orchestrator-side-sheet.component.ts` | Project chat, task tab, roadmap intake, attachments, make-task action |
| Reusable chat | `frontend/src/app/components/chat/chat.component.ts` | Markdown messages, attachments, sticky bottom, composer behavior |
| CLI usage and quota | `frontend/src/app/components/cli-usage-sheet.ts`, `quota-strip.ts`, `usage-hover-panel.ts`, `status-bar.ts` | Subscription windows, sessions, model picker, workspace token rollup |
| Token detail | `token-summary-block.ts`, `workspace-token-timeline.ts`, `JobInfo.tokenSummary`, `lastUsage` | Project totals, workspace timeline, per-job bubble, session usage |
| VS Code layout work | `docs/mockups/vscode-layout/`, `Frontend:VsCodeLayout` | Status bar, compact chrome, meta-panel direction, feature-flagged rollout |
| Chat workbench research | `docs/mockups/chat-window-next-gen/workbench-layout-research.md` | Chat-only, Chat plus Result, Chat plus Git, Chat plus Preview, Chat plus Debug presets; summary strip; compact icon actions |

Any implementation that removes one of these without a replacement is not aligned with the mockup.

## Integration Strategy

Use two feature boundaries:

1. `Frontend:VsCodeLayout` remains the app-shell and density flag.
2. Add a separate `Frontend:NextGenChat` flag for the conversation grammar and renderer.

Keeping the flags separate lets the chat improve inside today's layout first, then inherit the denser VS Code chrome when that shell is ready. The final product can turn both on together.

## Architecture

Add a pure projection layer before changing the visible layout:

```text
CliOutputLine[] + RunTimeline + JobInfo + screenshots + token summaries
  -> ConversationEvent[]
  -> shared conversation renderer
```

The projection must be pure TypeScript with unit tests. It should not read DOM state, services, or localStorage. Hosts pass in the evidence they already have.

Recommended event kinds:

- `message.user`
- `message.taskAgent`
- `message.orchestrator`
- `message.supervisor`
- `message.supportingAgent`
- `toolBurst`
- `supervisor.wait`
- `agent.needsInput`
- `system.captureFail`
- `system.parserWarning`
- `taskMarker`
- `runMarker`
- `decision`
- `warning`
- `artifact`
- `artifact.image`
- `metric`
- `workbench.summary`
- `workbench.gitPreview`
- `workbench.visualPreview`
- `traceLink`

Token usage should become `metric` events only when it helps the current conversation. The full token story stays in Status Bar, CLI Usage, Workspace Token Timeline, Project Detail, and Verbose Debug.

## Host Mapping

### Task Detail

The first task implementation should land inside the existing Protocol pane's Activity tab. Do not create a new top-level chat window.

Migration shape:

1. Keep the Protocol and Activity tabs in place.
2. Under `Frontend:NextGenChat`, replace only the Activity tab body with the new conversation renderer.
3. Keep Trace mode as the raw fallback.
4. Keep the run timeline available, but render run boundaries as slim markers in the transcript and move detailed run filters into an inspector or expansion.
5. Keep the existing composer behavior. Move start, stop, model, config, and mode controls into a compact composer toolbar only after projection and rendering are stable.
6. Show token usage as a compact context chip or expanded row. Do not remove the existing token surfaces.
7. Add the v7 workbench split host after the shared renderer is stable. The split presets are Chat only, Result, Git, Preview, and Debug. The right pane is a preview and drill-down launcher, not a replacement for Files, Commits, Screenshots, token details, or Verbose Debug.
8. Persisting split ratios and draggable resizing can wait. The first slice should use deterministic presets so Playwright can lock behavior and the UI does not become a general window manager.

### Project Side Sheet

The side sheet already owns project chat and task-scoped follow-up. It should reuse the same message grammar but does not need the raw CLI projection in the first slice.

Migration shape:

1. Adapt `ChatMessage` into the same visual message components used by task chat.
2. Keep the project picker, task tab, roadmap intake tab, attachments, and make-task action.
3. Add the same compact actor labels, markdown density, attachment grid, and light/dark theme tokens.
4. Allow a wider persisted side-sheet width later. Do not make it a separate global chat route.

### Verbose Debug

Verbose Debug is the escape hatch for dense operational data:

- actor counts
- orchestrator actions
- supervisor advisories
- run timing
- tool density
- warning density
- task markers
- token usage
- artifacts and screenshots
- raw trace links

It should be opened from task chat and side sheet chat, but it stays read-only and has no composer.

## Token Usage Rule

Tokens are important enough to keep as a first-class product surface, but they should not dominate the normal chat.

Default chat:

- show small per-run or per-decision token chips only when available
- show "open token detail" links where relevant
- do not render a token dashboard inline

Debug and project surfaces:

- keep Status Bar quota hover and CLI Usage sheet
- keep Workspace Token Timeline
- keep project `Tokens & cost`
- add Verbose Debug token panels for per-actor and per-run breakdowns

## Rollout Order

1. Add `Frontend:NextGenChat`, pure `ConversationEvent` projection, and fixture-based tests from real Stable logs.
2. Copy representative fixture fragments from the audited Activity Logs into tests for tool bursts, watchdog wait/kill, needs-input, capture-fail, duplicate sentinel, image evidence, test retry, token spike, and schema drift.
3. Swap the task Activity tab renderer behind the flag while preserving Trace, run timeline, auto-eval banner, and composer.
4. Add tool-burst rows and decision rows on top of the projection.
5. Add actor rails and labels in the shared renderer.
6. Adapt the project side sheet to the shared renderer and theme tokens.
7. Move model, start, stop, config, context chips, and mode controls into the compact composer toolbar.
8. Add the task Chat workbench split presets and summary strip: Chat only, Result, Git, Preview, Debug; state, token, commit, file, screenshot, warning, and duration chips.
9. Add Verbose Debug using the same projected events plus run, token, artifact, and screenshot aggregates.
10. Add Playwright coverage for current layout off, NextGenChat on, VsCodeLayout on, both flags on, light theme, dark theme, mobile, side sheet wide, wait-loop scenario, image lightbox, schema-drift row, workbench Git split, workbench compact density, chat-only preset, and Verbose Debug.

## Risk Controls

- Keep the old Activity Log path behind the flag until Playwright covers the real Stable evidence jobs.
- Use the existing `activity-log.parser` tests as the baseline; add projection tests instead of deleting parser behavior.
- Do not move token, quota, or session surfaces until equivalent links and tests exist.
- Do not let run timeline, auto-eval, composer, or popovers overlap. Layout reservation is part of the acceptance criteria.
- Keep raw technical output one click away. Hiding noise by default is good; deleting trace access is not.
- Do not build a full arbitrary docking system in the first implementation. Use named workbench presets and revisit draggable layout only after the workflow proves itself.

## Queue Alignment

The queued work should be treated as a staged migration:

1. `chat-layout-integration-bridge` should run before the chat implementation tasks. It creates the feature flag, shared event contract, mapping tests, host inventory, and fixture list from [activity-log-edge-cases.md](activity-log-edge-cases.md).
2. `chat-conversation-event-projection` builds the projection and first task-host rendering, including parser warnings and raw trace links.
3. `chat-tool-burst-collapsing` adds dense tool rows without changing the host structure.
4. `chat-actor-decision-cards` adds actor and decision grammar for orchestrator, supervisor, needs-input, capture-fail, and schema-drift rows.
5. `chat-verbose-debug-view` adds the secondary read-only developer view with actor counts, timing, tokens, tool density, image evidence, and warnings.
6. `chat-window-playwright-regression-suite` locks the whole migration with screenshots and interaction tests, including v7 workbench states and the v6 edge-case taxonomy.

The VS Code layout task remains the app-shell foundation. Chat work must read `docs/mockups/vscode-layout/taxonomy.md`, but it must not wait for the full shell if the Activity tab can improve safely behind `Frontend:NextGenChat`.
