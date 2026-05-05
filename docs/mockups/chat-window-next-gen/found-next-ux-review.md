# Found Next Workbench UX Review

This note treats the Angular prototype as the reference implementation seed, not as a throwaway mockup. The current iteration pulls shell concerns into smaller components and records the visual review loop that should keep the prototype from drifting away from the real product.

## VS Code Research Inputs

Primary sources:

- VS Code UX Guidelines overview: https://code.visualstudio.com/api/ux-guidelines/overview
- VS Code Status Bar guidance: https://code.visualstudio.com/api/ux-guidelines/status-bar
- VS Code Views guidance: https://code.visualstudio.com/api/ux-guidelines/views
- VS Code Custom Layout documentation: https://code.visualstudio.com/docs/configure/custom-layout
- VS Code User Interface documentation: https://code.visualstudio.com/docs/getstarted/userinterface

Useful rules for this prototype:

- Put workspace-level status on the left side of the status bar and active-context controls on the right.
- Keep status bar labels short. Use icons only when the metaphor is clear.
- Treat the center work area as the high-value surface. Side bars, panes, panels, and status affordances support it.
- Keep view counts low. Prefer reusable views and tree/list structures over many custom one-off webview-like panels.
- Make layout controls compact, persistent, and scoped to the surface they affect.
- Let users hide or move supporting surfaces. The prototype should model optional chat, optional side sheet, and additive review panes without becoming a full docking manager.

## Refactor Slice

The prototype now has a first component boundary:

| Area | File | Responsibility |
|------|------|----------------|
| Shell top bar | `frontend/src/app/components/mockups/found-next-topbar.component.ts` | Product title, project filter chips, owner switch, run summary, sheet/queue/density/theme/command/debug controls. |
| Shell status bar | `frontend/src/app/components/mockups/found-next-statusbar.component.ts` | Global run health, automation mode, session continuity, Codex/Claude 5h quota strip, token/git/visual/tool signals, model defaults. |
| Shared data | `frontend/src/app/components/mockups/next-gen-chat-workbench-prototype.data.ts` | Topbar project tabs, run stats, status usage strip, shared icon paths. |
| Shared models | `frontend/src/app/components/mockups/next-gen-chat-workbench-prototype.models.ts` | Typed pane, actor, scenario, decision, status, density, theme, and transcript contracts. |
| Workbench host | `frontend/src/app/components/mockups/next-gen-chat-workbench-prototype.component.ts` | Task list, detail chrome, chat flow, rail, panes, popovers, modals, and orchestration state. |

This is intentionally not a full rewrite. The next useful boundaries are `ActivityRail`, `TaskQueueList`, `WorkbenchPaneHost`, `ConversationTranscript`, `ComposerBar`, `StatusPopover`, and `VerboseDebugModal`.

## Component UX Pass

| Component | What works | Current risk | Next iteration |
|-----------|------------|--------------|----------------|
| Activity bar | Recognizable VS Code-like rail, compact, icon-led. | It is currently navigation-like but not yet tied to actual project-level surfaces. | Add active surface labels in tooltips and a single customize-layout action. |
| Top bar | Project chips, owner switch, density/theme/debug controls preserve useful existing app concepts. | It can still become crowded on small widths. | Keep only project/owner and current-run essentials visible by default; move secondary actions behind command or layout menu if crowded. |
| Task queue | Narrow and useful for local task context. | Cards still feel more mockup-like than app-like because they lack real lane grouping and ownership signals. | Split into `TaskQueueList` with lane chips, owner, CLI type, and hidden long metadata. |
| Detail chrome | Keeps current app actions like `Complete & Next` visible. | The run summary duplicates status bar information in some states. | Make the chrome about task action only; move global run health to status bar and side rail. |
| Inspector rail | Strong concept for panes, cases, and signals. | It mixes view selection, scenario demo controls, and metrics. | Separate production controls from scenario/test controls. Scenario controls should be hidden in a prototype-only rail group. |
| Chat transcript | Actor grammar is much clearer than raw Activity Log. | Some messages still read like requirement cards rather than natural chat. | Reduce default message height, collapse long agent text earlier, and keep technical paths behind disclosure. |
| Decision rows | Good compact home for orchestrator and supervisor decisions. | Retry and evidence labels can compete with chat text. | Use one-line summary plus an explicit `Details` disclosure. Evidence goes into debug or popover. |
| Composer | Good density: mode, context chips, model, start/pause/continue in one place. | Buttons are numerous and can become visually equivalent. | Group mutating actions, emphasize primary action, and put lower-frequency controls in a small menu. |
| Context panes | Additive panes solve the side-by-side review need. | The pane host is still one large component and can feel like a dashboard when all panes are open. | Extract pane host and tune each pane for quick review, not full replacement of existing tabs. Treat multi-pane review as focus mode: when two or more panes open, the project side sheet yields space and remains one click away. |
| Project side sheet | Keeps project-level steering separate from task chat. | It needs stronger connection to project switch and owner filter. | Show active project set, owner filter, and side-sheet scope in one compact header. |
| Status bar | Now closer to VS Code: tiny, bottom-aligned, short labels, global left and contextual right. Codex and Claude show percentage pressure plus the 5h quota window because these numbers affect routing decisions. | It is information-dense and could violate VS Code's "limit items" guidance. | Decide which items are always visible versus overflow. Keep tokens, session, Codex %, Claude %, and 5h reset context visible because they are product-defining. |
| Popovers and modals | Good drill-down pattern. | Some popovers are too wide and content-heavy for routine use. | Make popovers command-like: dense rows, one primary metric, fast navigation to full debug. |
| Dark theme | Much improved after the last pass. | Some colors still use light-first contrast assumptions. | Keep color tokens shared and test every primary state in dark screenshots. |

## Review Loop Candidate

This can become a reusable skill or checklist later:

1. Name the component boundary and the production surface it maps to.
2. Check density: can the user still see the chat, Git, or result surface without top-heavy chrome?
3. Check scope: is the control global, project-level, task-level, run-level, or message-level?
4. Check VS Code alignment: side bar for navigation, editor/workbench for work, status bar for short state, command/menu for less-used actions.
5. Check text fit at 390px, 900px, 1440px, light theme, and dark theme.
6. Check if every icon has a clear tooltip or visible label in comfortable mode.
7. Check if technical detail is reachable but not default.
8. Check if dummy data is typed and reusable rather than embedded in the host component.
9. Capture Playwright screenshots for primary, compact, dark, no-chat, all-panes, and mobile states.
10. Write the next concrete extraction or polish step before adding more visual features.

## Next Extraction Order

1. `ActivityRail`: move activity items and rail styling out of the host.
2. `StatusPopover`: move status panel content and handoff rows out of the host.
3. `ConversationTranscript`: move actor/decision rendering into a reusable renderer.
4. `ComposerBar`: make model/defaults/start/stop/configuration a reusable control surface.
5. `WorkbenchPaneHost`: isolate chat/result/git/preview/debug pane toggling and splitter behavior.

The goal is a reference that can be implemented incrementally in the real app. Each extracted component should keep the same test ids until the production integration has its own stable selectors.
