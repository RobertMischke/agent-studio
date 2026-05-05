# Angular Clickware Prototype

This document records the Angular-hosted prototype for the next-generation task chat workbench.

The static v7 HTML mockup remains the fast visual reference. The Angular prototype goes one step further: it uses the real Angular component model and signals for interaction state, but it now runs as a standalone mockup application. It does not mount inside the normal dev frontend and it does not use the per-checkout DEV banner.

## How To Open

Run the standalone mockup app from `frontend/`:

```sh
npm run mockup:chat
```

Open:

```text
http://127.0.0.1:4022
```

The normal dev app continues to run on its own port. Do not use `atp.flag.nextGenChatPrototype` to replace the real app shell during design review.

## Files

| File | Purpose |
|------|---------|
| `frontend/src/app/components/mockups/next-gen-chat-workbench-prototype.component.ts` | Standalone Angular workbench host for task list, detail, transcript, panes, popovers, and modals. |
| `frontend/src/app/components/mockups/found-next-topbar.component.ts` | Extracted shell top bar for project filter, owner switch, run summary, density/theme, command, debug, and side-sheet controls. |
| `frontend/src/app/components/mockups/found-next-statusbar.component.ts` | Extracted shell status bar for run health, automation state, session continuity, usage, tokens, Git, visual evidence, and model defaults. |
| `frontend/src/app/components/mockups/next-gen-chat-workbench-prototype.data.ts` | Shared dummy data and icon paths used by the shell components and host. |
| `frontend/src/app/components/mockups/next-gen-chat-workbench-prototype.models.ts` | Shared type contracts for panes, actors, scenarios, decisions, status panels, density, theme, and transcript events. |
| `frontend/src/mockups/next-gen-chat/main.ts` | Standalone Angular bootstrap for the mockup app. |
| `frontend/src/mockups/next-gen-chat/index.html` | Dedicated mockup host document. |
| `frontend/angular.json` | Defines the `next-gen-chat-mockup` app and serve target. |
| `frontend/e2e/next-gen-chat-angular-prototype.spec.ts` | Playwright coverage and screenshot capture. |

## Why This Shape

The user wants to evaluate the next layout without disrupting the current application. The prototype must not steal the normal dev port, inherit the DEV marker, or depend on localStorage flags inside the production shell. The lower-interference path is:

1. Keep the prototype in the dev checkout.
2. Serve it as its own Angular app on a separate port.
3. Make it full-screen and self-contained.
4. Keep production task chat, side sheet, Activity Log, Trace, Git, screenshots, and token surfaces unchanged.
5. Use Playwright screenshots as local review evidence only.

This follows the repo's product boundary: the app itself should not grow branch/worktree orchestration. A future engineering workflow may still use a normal Git worktree outside the product when multiple agents need isolated source checkouts, but that is a contributor workflow, not product behavior.

## Interaction Coverage

The latest iteration is a tall workbench. Task tabs, summary metrics, split presets, and scenario switches moved out of horizontal top bands into a narrow left task rail. Chat and the adjacent Result, Git, Preview, or Debug pane now start directly under a compact task-detail chrome and run down to the status bar. This is the working target for the production handoff: normal chat and adjacent review panes should use roughly the full available task height.

The nav and status-bar iteration adds a more VS Code-like control model. The activity bar is clickable, the top chrome now shows only run summary plus global project sheet and queue shortcuts, and the status bar becomes a dense action surface instead of a passive label. Queue, token usage, health, visual evidence, and CLI/model configuration open as lightweight bottom popovers. The goal is to keep professional working height for chat and adjacent panes while still making observability, tokens, automation state, and run configuration one click away.

The next iteration treats every workbench pane as optional and additive. Chat is no longer assumed to be permanent: it can be closed while Git review, Result, Preview, or Debug stays open. Git review now owns the source/editor preview, with a changes list on the left and the selected source diff on the right. The old range slider is gone; width is modeled by a real vertical splitter with keyboard support.

The document-model iteration reframes those panes as opened workbench documents. The left rail behaves more like a VS Code view container: it exposes documents and run facts, while the center work area shows a tab strip for `Summary`, `Task Chat`, `Git changes`, `Screenshots`, and `Debug trace`. `Summary` is the default dashboard document and should quickly explain phase, risk, evidence, and the next useful drill-down without pretending to be the full detail view.

The queue iteration applies the same module rule to the task list. Queue selection is no longer treated as permanent chrome: it is a `Tasks / Queue` activity module with an explicit close button, and the Activity Bar can reopen it when the user wants to switch tasks. When closed, the task detail, chat documents, Git review, and side sheet reclaim the width.

The rail now has inline icons, visible labels in comfortable density, tooltip titles, a `Task rail` guide button, and an explicit open-pane count on the "All" pin action. Compact density collapses the same controls back to icons so the chat area stays large. The detail header and top bar no longer duplicate the pane switcher; the rail owns pane pinning, the top bar owns global sheet and queue toggles, and the status bar owns runtime controls. The task list is intentionally narrower and only carries queue selection, not pane controls. Because the prototype is a separate Angular app, the normal frontend does not pay for it and cannot accidentally show it.

The prototype currently covers:

- Task detail shell with project chip, editable-title affordance, state pill, Complete & Next, narrow queue list, optional chat, side sheet, and status bar.
- Optional Queue module that can be closed from the queue header and reopened through the Activity Bar.
- Workbench panes: Result, Git plus source diff, Preview, Debug, and optional Chat.
- Workbench document tabs for Summary, Task Chat, Git changes, Screenshots, and Debug trace.
- Left task rail with additive pane pinning, quick signal jumps, run state, tokens, commits, files, screenshots, failed retry, duration, and scenario switches.
- Rail guide modal that explains the control model.
- Clickable activity bar and top chrome for project side sheet, queue, run summary, density, theme, command palette, and debug.
- Interactive status bar with queue, token, health, visual evidence, CLI/model, density, theme, and command-palette entry points. Codex and Claude quota pills show percent pressure and the 5h window because that window directly affects routing and continuation choices.
- Status-bar popovers for queue automation, token heat, system health, evidence shortcuts, and model controls.
- Optional chat toggle, additive context-pane toggles, "pin all" review panes, and a vertical splitter between chat and pinned review panes.
- Clickable run marker popover with CLI, model, session, trace range, outcome, token budget, and artifact counts.
- Scenario controls: Review, Tool burst, Wait loop, Images, Drift.
- Decision scenario covering reissue, heuristic, needs-input, circuit breaker, capture-fail, and schema-drift rows.
- Tool-burst expansion.
- Composer command deck with Continue, Extend task, Steer, Follow-up job, attachments, context chips, permissions, CLI, model, Start, Pause, and send action.
- Git change preview next to chat.
- Screenshot lightbox.
- Verbose Debug modal with Overview, Actors, Tools, Tokens, and Trace filters.
- Light and dark theme.
- Comfortable and compact density.
- Mobile collapse.
- Persistent actor rails for User, Task Agent, Orchestrator, Supervisor, Supporting Agent, Tool Runner, and System with non-color cues (icon, glyph, shape, label, accent stripe).
- Compact decision rows for reissue, heuristic, needs-input, circuit breaker, capture fail, and schema drift, expandable to reason / evidence / action / retry budget / token usage / next step plus a Trace link.
- Target-aware user intervention chips (current run, next run, orchestrator, follow-up task) on user turns.

The current refactor iteration starts turning the clickware into a component reference. Topbar and statusbar are now standalone Angular components, while topbar/statusbar dummy data and shared icon paths live in a separate data module. The next extraction should continue downward into `ActivityRail`, `StatusPopover`, `ConversationTranscript`, `ComposerBar`, and `WorkbenchPaneHost`, keeping the implementation slice small enough to visually review after every step.

The UX review now names the target visual system as the internal Found Next Workbench Framework. This is a rule set and component boundary, not a third-party package: use VS Code workbench containers, local theme tokens, compact density primitives, screenshot-driven review, and reusable Angular components instead of importing a deprecated VS Code webview toolkit or a generic dashboard UI kit.

## Screenshots

Playwright can still generate screenshots under `docs/mockups/chat-window-next-gen/evidence/`, but those PNG files are local review output and are gitignored. Do not commit regenerated screenshots from this mockup folder.

## Next Step

Use this prototype as a clickable handoff for the queued `Frontend:NextGenChat` implementation tasks. The production implementation should not copy the prototype wholesale. It should extract the proved interaction model and re-implement it against real `ConversationEvent` data, existing task evidence, and existing side-sheet behavior.

The key layout rule from this iteration is vertical discipline: do not put task metadata, tokens, run state, or scenario controls into stacked horizontal bars above the transcript. Put them in the left rail, the pane header, the composer toolbar, the status bar, or a drill-down surface. The chat itself is also optional: professional review mode must support closing chat and using the available height for Git changes plus source editor/diff. Pane visibility is additive, but it is not a full docking system.
