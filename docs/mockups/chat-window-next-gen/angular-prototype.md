# Angular Clickware Prototype

This document records the first Angular-hosted prototype for the next-generation task chat workbench.

The static v7 HTML mockup remains the fast visual reference. The Angular prototype goes one step further: it runs inside the real frontend shell, uses Angular signals for interaction state, and can be opened through a local feature flag without replacing production chat behavior.

## How To Open

Enable the prototype from the browser console:

```js
localStorage.setItem('atp.flag.nextGenChatPrototype', '1');
location.reload();
```

Disable it:

```js
localStorage.removeItem('atp.flag.nextGenChatPrototype');
location.reload();
```

The prototype can also close itself through the `X` action, which clears the flag.

## Files

| File | Purpose |
|------|---------|
| `frontend/src/app/components/mockups/next-gen-chat-workbench-prototype.component.ts` | Standalone Angular clickware component. |
| `frontend/src/app/services/feature-flags.service.ts` | Adds the local `atp.flag.nextGenChatPrototype` flag. |
| `frontend/src/app/app.ts` | Mounts the prototype as a full-screen overlay only when the flag is enabled. |
| `frontend/e2e/next-gen-chat-angular-prototype.spec.ts` | Playwright coverage and screenshot capture. |

## Why This Shape

The user wants to evaluate the next layout without disrupting the current application. A separate branch or worktree can help when several implementation agents edit the same production files, but it is not necessary for this clickware layer. The lower-interference path is:

1. Keep the prototype in the dev checkout.
2. Gate it with an explicit local flag.
3. Make it full-screen and self-contained.
4. Keep production task chat, side sheet, Activity Log, Trace, Git, screenshots, and token surfaces unchanged while the flag is off.
5. Use Playwright screenshots as review evidence.

This follows the repo's product boundary: the app itself should not grow branch/worktree orchestration. A future engineering workflow may still use a normal Git worktree outside the product when multiple agents need isolated source checkouts, but that is a contributor workflow, not product behavior.

## Interaction Coverage

The latest iteration is a tall workbench. Task tabs, summary metrics, split presets, and scenario switches moved out of horizontal top bands into a narrow left inspector rail. Chat and the adjacent Result, Git, Preview, Debug, or Source pane now start directly under a compact task-detail chrome and run down to the status bar. This is the working target for the production handoff: normal chat and adjacent review panes should use roughly the full available task height.

The nav and status-bar iteration adds a more VS Code-like control model. The activity bar is clickable, the top chrome exposes task chat, Git split, project side sheet, and queue shortcuts, and the status bar becomes a dense action surface instead of a passive label. Queue, token usage, health, visual evidence, and CLI/model configuration open as lightweight bottom popovers. The goal is to keep professional working height for chat and adjacent panes while still making observability, tokens, automation state, and run configuration one click away.

The rail now has inline icons, visible labels in comfortable density, tooltip titles, and a `Workbench` guide button that explains why the controls live there. Compact density collapses the same controls back to icons so the chat area stays large. The prototype is also deferred behind the feature flag, so the normal app does not pay the prototype bundle cost when the flag is off.

The prototype currently covers:

- Task detail shell with project chip, editable-title affordance, state pill, existing pane toggles, Complete & Next, task list, Chat tab, side sheet, and status bar.
- Workbench panes: Result, Git, Preview, Debug, Source, and Chat-only.
- Left inspector rail with task tabs, run state, tokens, commits, files, screenshots, failed retry, duration, split presets, and scenario switches.
- Rail guide modal that explains the control model.
- Clickable activity bar and top navigation for task chat, Git split, project side sheet, and queue.
- Interactive status bar with queue, token, health, visual evidence, CLI/model, density, theme, and command-palette entry points.
- Status-bar popovers for queue automation, token heat, system health, evidence shortcuts, and model controls.
- Clickable run marker popover with CLI, model, session, trace range, outcome, token budget, and artifact counts.
- Scenario controls: Review, Tool burst, Wait loop, Images, Drift.
- Tool-burst expansion.
- Composer command deck with Continue, Extend task, Steer, Follow-up job, attachments, context chips, permissions, CLI, model, Start, Pause, and send action.
- Git change preview next to chat.
- Screenshot lightbox.
- Verbose Debug modal with Overview, Actors, Tools, Tokens, and Trace filters.
- Light and dark theme.
- Comfortable and compact density.
- Mobile collapse.

## Screenshots

The durable screenshots live in `docs/mockups/chat-window-next-gen/evidence/`:

- `next-gen-chat-angular-prototype-result.png`
- `next-gen-chat-angular-prototype-nav-queue.png`
- `next-gen-chat-angular-prototype-status-tokens.png`
- `next-gen-chat-angular-prototype-status-health.png`
- `next-gen-chat-angular-prototype-status-model.png`
- `next-gen-chat-angular-prototype-run-popover.png`
- `next-gen-chat-angular-prototype-rail-guide.png`
- `next-gen-chat-angular-prototype-git.png`
- `next-gen-chat-angular-prototype-compact.png`
- `next-gen-chat-angular-prototype-lightbox.png`
- `next-gen-chat-angular-prototype-debug-dark.png`
- `next-gen-chat-angular-prototype-mobile.png`

## Next Step

Use this prototype as a clickable handoff for the queued `Frontend:NextGenChat` implementation tasks. The production implementation should not copy the prototype wholesale. It should extract the proved interaction model and re-implement it against real `ConversationEvent` data, existing task evidence, and existing side-sheet behavior.

The key layout rule from this iteration is vertical discipline: do not put task metadata, tokens, run state, or scenario controls into stacked horizontal bars above the transcript. Put them in the left inspector, the pane header, the composer toolbar, the status bar, or a drill-down surface.
