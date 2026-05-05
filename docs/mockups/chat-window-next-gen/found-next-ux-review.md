# Found Next Workbench UX Review

This report treats the Angular prototype as the reference implementation seed, not as a throwaway mockup. The current goal is to find a visual framework that can survive production integration: compact enough for expert work, explicit enough for orchestration state, and close enough to VS Code that the layout feels familiar instead of decorative.

## Research Inputs

Primary sources:

- VS Code UX Guidelines overview: https://code.visualstudio.com/api/ux-guidelines/overview
- VS Code Extending Workbench documentation: https://code.visualstudio.com/api/extension-capabilities/extending-workbench
- VS Code Status Bar guidance: https://code.visualstudio.com/api/ux-guidelines/status-bar
- VS Code Views guidance: https://code.visualstudio.com/api/ux-guidelines/views
- VS Code Activity Bar guidance: https://code.visualstudio.com/api/ux-guidelines/activity-bar
- VS Code Sidebars guidance: https://code.visualstudio.com/api/ux-guidelines/sidebars
- VS Code Panel guidance: https://code.visualstudio.com/api/ux-guidelines/panel
- VS Code Editor Actions guidance: https://code.visualstudio.com/api/ux-guidelines/editor-actions
- VS Code Quick Picks guidance: https://code.visualstudio.com/api/ux-guidelines/quick-picks
- VS Code Webviews guidance: https://code.visualstudio.com/api/ux-guidelines/webviews
- VS Code Custom Layout documentation: https://code.visualstudio.com/docs/configure/custom-layout
- VS Code User Interface documentation: https://code.visualstudio.com/docs/getstarted/userinterface
- VS Code Webview UI Toolkit repository: https://github.com/microsoft/vscode-webview-ui-toolkit
- Code-OSS repository: https://github.com/microsoft/vscode
- Angular Components / CDK repository: https://github.com/angular/components
- Fluent UI Web Components documentation: https://learn.microsoft.com/en-us/fluent-ui/web-components/
- Design-system comparison: [design-system-options.md](design-system-options.md)

Research takeaways:

1. VS Code divides the workbench into containers and items. The useful mapping for this product is Activity Bar, Side Bar, Editor Group, Panel, and Status Bar rather than cards stacked in a dashboard.
2. Status Bar items should be short and limited. Global workspace signals belong left; contextual controls belong right. Our product has a special exception: Codex and Claude quota pressure are route-affecting operational state, so they must stay visible even when compacted.
3. Views should usually be lists, trees, and scoped panes. They should not become buttons disguised as tree rows, and they should not repeat existing functionality.
4. Webviews are powerful but should be used only when necessary. For our app this means: custom Angular panes are fine because this is the app itself, but they should still behave like workbench surfaces, not like isolated marketing webviews.
5. The VS Code Webview UI Toolkit has useful principles: themeability, accessibility, and consistent component language. It is deprecated, so it should not become a dependency. Use it as a reference, not as a framework.
6. VS Code's document model is the better analogy for task artifacts: left-side views help users find things, while the editor area owns opened documents with tabs, split groups, and close behavior.
7. The Activity Bar should open or focus View Containers. This confirms the latest Queue change: the Queue is a `Tasks / Queue` module, not a permanent left column.
8. Sidebars should group related Views and avoid excessive View counts. The task workbench should keep only the task Queue and the task rail visible by default; rarer surfaces move to command, status popover, or debug.
9. Editor actions should be contextual and sparse. Pane headers should keep one or two document actions visible and send rarer commands to overflow, command palette, or contextual popover.
10. Quick Pick style interactions are the right pattern for model, owner/project, artifact jump, and target-scope choices. They should not become permanent config panels.

## Visual Framework Decision

Use an internal **Found Next Workbench Framework** instead of adopting an external component kit.

The framework is not a new library yet. It is a rule set plus reusable Angular components:

- **Layout containers:** `ActivityBar`, `TaskQueueList`, `TaskDetailChrome`, `InspectorRail`, `WorkbenchPaneHost`, `ProjectSideSheet`, `StatusBar`.
- **Primitive density:** 28px status rows, 30 to 34px tool rows, 38px task chrome, 48px activity rail, 6 to 8px local padding, 1px borders, 6 to 8px radius only on controls and repeated cards.
- **Theme tokens:** app-level CSS variables for `bg`, `chrome`, `surface`, `surface-soft`, `line`, `line-strong`, `text`, `muted`, `accent`, `ok`, `warn`, `danger`, `teal`, and `purple`.
- **Interaction grammar:** visible labels in comfortable density, icon-first compact density, every icon button with title or aria-label, technical detail behind disclosure, and no stacked horizontal metadata bands above the transcript.
- **Evidence rule:** every visual iteration must have light, compact, no-chat Git, all-tabs, dark, status-popover, owner-popover, and mobile screenshots.

Decision: do not import `@vscode/webview-ui-toolkit`, shadcn, Material, or another heavy UI kit for this prototype. The app has specialized workbench requirements, already uses Angular standalone components, and needs tight control over density. A small internal framework gives better fit and avoids a deprecated VS Code dependency.

The production path is narrower than "clone VS Code": inspect Code-OSS for measurement and behavior, use Codicons or local codicon-derived symbols for the small-icon vocabulary, use Angular CDK / Angular Aria for overlays, focus, splitter, menu, dialog, and keyboard behavior, and keep the visible layer in owned Found Next CSS tokens. The explicit comparison and guardrails live in [design-system-options.md](design-system-options.md).

## Panel And Document Model Trial

The prototype should now explore a VS-Code-like panel/document model:

- Activity Bar items open view containers.
- The left task rail behaves like a contextual view, similar to Explorer, Source Control, or Chat.
- Clicking a rail item opens or focuses a workbench document.
- The center work area shows opened documents in a tab strip.
- `Summary` is the default document and stays optimized for phase, risk, evidence, and next best action.
- `Task Chat`, `Git changes`, `Screenshots`, and `Debug trace` are specialized documents that can be opened, focused, split visually, or closed.

This is different from the older "chat plus adjacent panes" mental model. The old model treated every surface as a pane next to chat. The document model says chat is only one document. Git review can become the active document, Summary can remain the default dashboard, and Debug can be opened only when the user needs depth.

Open question: whether production should keep one editor group only, or support two editor groups for side-by-side document review. The current prototype keeps the simpler path: one tab strip, one active document, and full content per tab. Split review should be an explicit editor-group action later, not an accidental side effect of opening several tabs.

## Screenshot Evidence

Playwright regenerates these local files under `docs/mockups/chat-window-next-gen/evidence/`. The folder is review output and is gitignored.

| Screenshot | Purpose | Pass criteria |
|------------|---------|---------------|
| `next-gen-chat-angular-prototype-result.png` | Default light workbench with chat, result pane, side sheet, topbar, and statusbar. | Chat starts high, result pane is readable, side sheet is supportive, statusbar carries runtime state. |
| `next-gen-chat-angular-prototype-document-tabs.png` | Future named capture for the panel/document model. | Summary is the default document; Chat, Git, Preview, and Debug are visible as opened workbench documents. |
| `next-gen-chat-angular-prototype-status-tokens.png` | Token and quota popover. | Codex and Claude percentages plus 5h window and reset context are visible without turning the chat into a dashboard. |
| `next-gen-chat-angular-prototype-project-owner.png` | Project and owner popover. | Owner is Robert, project tabs remain compact, and owner filtering does not steal transcript height. |
| `next-gen-chat-angular-prototype-all-panes.png` | All review tabs open. | Summary, Chat, Git, Screenshots, and Debug are visible as tabs, but only the active document owns the content area. |
| `next-gen-chat-angular-prototype-git-no-chat.png` | Git plus source diff with chat closed. | Git changes own the left list and source diff owns the right editor surface. |
| `next-gen-chat-angular-prototype-dark-workbench.png` | Dark theme with multiple panes. | Primary states stay legible and no light-theme assumptions break contrast. |
| `next-gen-chat-angular-prototype-mobile.png` | 390px mobile collapse. | No clipped primary actions; compact usage still shows Codex and Claude quota pressure. |

## Refactor Slice

The prototype now has a first component boundary:

| Area | File | Responsibility |
|------|------|----------------|
| Shell top bar | `frontend/src/mockups/next-gen-chat/app/found-next-topbar.component.ts` | Product title, project filter chips, owner switch, run summary, sheet/queue/density/theme/command/debug controls. |
| Shell status bar | `frontend/src/mockups/next-gen-chat/app/found-next-statusbar.component.ts` | Global run health, automation mode, session continuity, Codex/Claude 5h quota strip, token/git/visual/tool signals, model defaults. |
| Activity Bar | `frontend/src/mockups/next-gen-chat/app/next-gen-chat-activity-bar.component.ts` | Global module switcher for Projects, Tasks, Search, Git, QA, Tokens, and close. |
| Queue module | `frontend/src/mockups/next-gen-chat/app/next-gen-chat-queue.component.ts` | Optional queue sidebar with filters, active task, lane, order, agent, and metadata. |
| Task rail | `frontend/src/mockups/next-gen-chat/app/next-gen-chat-rail.component.ts` | Workbench documents, scenario cases, signal chips, and rail guide entry. |
| Document tabs | `frontend/src/mockups/next-gen-chat/app/next-gen-chat-document-tabs.component.ts` | Opened document list with activate and close behavior. |
| Context documents | `frontend/src/mockups/next-gen-chat/app/next-gen-chat-context-document.component.ts` | Summary, Git/source diff, Screenshots, and Debug document bodies. |
| Shared data | `frontend/src/mockups/next-gen-chat/app/next-gen-chat-workbench-prototype.data.ts` | Topbar project tabs, run stats, status usage strip, shared icon paths. |
| Shared models | `frontend/src/mockups/next-gen-chat/app/next-gen-chat-workbench-prototype.models.ts` | Typed pane, actor, scenario, decision, status, density, theme, document, queue, Git, token, and transcript contracts. |
| Workbench host | `frontend/src/mockups/next-gen-chat/app/next-gen-chat-workbench-prototype.component.ts` | Task detail shell, transcript, composer, status popovers, modals, and orchestration state. |
| Mockup global stylesheet | `frontend/src/mockups/next-gen-chat/styles.scss` | App-local Found Next theme tokens, density, grid, mobile, dark theme, and component styling. |

This is intentionally not a full rewrite. The next useful boundaries are `ConversationTranscript`, `ComposerBar`, `StatusPopover`, and `VerboseDebugModal`.

## Component Scorecard

Scores: 1 means weak fit, 5 means production-reference fit.

| Component | Metric fit | Value fit | Sense fit | Score | Audit |
|-----------|------------|-----------|-----------|-------|-------|
| Activity bar | 48px rail, icon-only, no vertical waste. | Good for global project surfaces and workbench navigation. | Sensible, but current targets are partly placeholder. | 4 | Keep as global container. Add exact target mapping before production build. |
| Top bar | 34px high, run stats compressed into pills. | Project tabs, owner Robert, run stats, layout controls are valuable. | Slightly crowded at desktop right edge, but acceptable. | 4 | Keep only project/owner/run essentials visible. Push rare actions into command menu if crowding returns. |
| Task queue | 196px comfortable, 172px compact, narrow cards, two-line titles. | Gives local task context without consuming editor width. | Sensible after extraction; lane, order, agent, and metadata are now visible without becoming a Kanban board. | 4 | Keep it as an optional module. Bind the filters to real lane state during production migration. |
| Detail chrome | 38px high, direct task action. | `Complete & Next` and state are essential. | Sensible, but run metrics duplicate statusbar in some states. | 3 | Make chrome about task actions only. Move global run health into statusbar and rail. |
| Inspector rail | 132px comfortable, 64px compact. | Panes, signals, and cases are useful during design review. | Mixed: production controls and prototype scenario controls share one rail. | 3 | Split production pane controls from prototype-only scenarios. |
| Chat transcript | Message cards are readable; actor chips are scannable. | Preserves human-level conversation while hiding technical layer. | Sensible, but some dummy copy still reads like requirements. | 4 | Collapse long agent text earlier and make technical paths disclosure-only. |
| Actor grammar | Icons, glyphs, labels, counts, and accents avoid color-only meaning. | Essential because user, agent, orchestrator, supervisor, support, tool, and system differ. | Strong and product-specific. | 5 | Keep this as a contract for production conversation projection. |
| Decision rows | One-line summary plus expandable details. | Reissue, heuristic, needs-input, circuit breaker, capture fail, and drift all fit. | Good, but retry/evidence can compete visually. | 4 | Keep details collapsed by default; move dense evidence to debug. |
| Composer | Context chips, CLI/model, start/pause/continue stay close to input. | Strong because model, permission, run controls, and follow-up mode are real workflow controls. | Useful, but many buttons have similar weight. | 3 | Group low-frequency controls behind a menu and keep one primary send action. |
| Workbench document host | Document tabs, optional chat, full-content active document, and all-tabs mode work. | Directly supports VS-Code-like task documents while keeping Summary as the default dashboard. | Strong, and the document bodies now have their own component boundary. Split editor groups still need a separate design pass. | 4 | Decide whether production keeps one editor group or supports explicit split editor groups. |
| Result pane | Metrics, human result, acceptance snapshot, function parity. | Useful after a run. | Current card density is still dashboard-like. | 3 | Convert to compact summary rows plus one expandable acceptance section. |
| Git pane | Changed files plus source diff. | Very high value for reviewing agent output beside chat. | Strongest current pane. | 5 | Keep Git as source-review pane, not generic source browser. |
| Preview pane | Evidence grid plus lightbox. | Useful when screenshots decide quality. | Sensible, but should appear only when visual evidence exists. | 4 | Bind to real result screenshots and hide empty preview pane by default. |
| Debug pane | Dense diagnostics separated from default chat. | Essential for developer understanding. | Sensible, but needs its own component and trace linking. | 4 | Extract `VerboseDebugModal` and `DebugPaneSummary`. |
| Project side sheet | Project-level steering separate from task chat. | High value for queue and cross-task context. | Good, and now yields space in multi-pane mode. | 4 | Add project/owner scope header and recent decisions summary. |
| Status bar | 28px, persistent, clickable, quota-aware. | Tokens, Codex %, Claude %, 5h window, session, Git, visual and model are product-defining. | Strong, though center group bends VS Code guidance. | 4 | Keep quota visible. Consider grouping center items into a primary left block if production crowding appears. |
| Status popovers | Bottom popovers keep details out of transcript. | Token heat, model, health, evidence, queue are important. | Useful, but some popovers are too panel-like. | 3 | Make popovers row-dense and link to full pages/debug for deeper analysis. |
| Dark theme | Primary surfaces are now readable. | Required, but light remains the primary user preference. | Mostly sensible. | 4 | Continue screenshot checks for every primary state. |
| Mobile collapse | Main actions remain reachable at 390px and the document area no longer drifts into an implicit off-screen grid column. | Useful for inspection, not primary work mode. | Works, but expert workflows remain desktop-first. | 3 | Preserve the Playwright mobile guard; do not optimize at the expense of desktop density. |

## Component Notes

### Activity Bar

The Activity Bar should behave like the VS Code Activity Bar: a global surface switcher, not a task-specific toolbar. The current icons are compact and do not steal height. The risk is semantic drift: if each icon opens a custom full-screen one-off, the workbench becomes a drawer collection. Production should map each icon to one durable project surface such as Queue, Search, Git, QA, Tokens, or Settings.

### Top Bar

The top bar should answer: which project set, which owner, which run, which layout mode. It should not own task pane switching. Robert is the correct owner label. The current value set is good: `Run 4`, `12m`, `28 tools`, `42k tokens`, `3 commits`. The metric risk is width: adding more pills will force wrapping or hide the title. Next iteration should add a hard rule: maximum five run-stat pills.

### Task Queue

The queue is useful because it preserves local context while the task detail is open. It should not try to be the full Kanban board. The current card metrics work, but the values need production shape: state, order, owner, CLI type, and maybe blocked/review indicators. Long descriptions should remain hidden.

### Inspector Rail

The inspector rail is the strongest structural answer to the user's height concern. It moves scenarios, metrics, and pane toggles out of the top. The issue is conceptual mixing: `Cases` are prototype controls, while `Panes` and `Signals` are production concepts. The next refactor should split these so a production version can compile without scenario-only controls.

### Chat Transcript

The transcript should be the humane layer. It should show what happened, who acted, and what changed. It should not show raw filesystem paths by default. The current actor model is good. The next text pass should make agent messages less like specification cards and more like concise run narration, with `Show technical layer` as the escape hatch.

### Composer

The composer correctly keeps model choice, permission scope, start/pause, and continuation together. This is important because the chat is not just text. It starts and steers agents. The risk is visual equivalence: too many buttons look equally important. The primary button should be the only dark/high-weight action; lower-frequency controls should become a small menu.

### Workbench Document Host

The document host should model expert review without implementing a full docking manager. Current behavior is right: Summary is the default document, chat can close, Git can own the work surface, and multi-document mode opens tabs rather than rendering every document at once. The next production decision is persistence: whether open documents are per-task, per-project, or session-only. Explicit split editor groups can come later.

### Status Bar

The status bar is now product-critical. It is not decoration. Codex and Claude percentage values plus the 5h window are routing signals; they affect whether the user should continue with a CLI, switch providers, wait for reset, or queue a supporting job. The VS Code guidance says to limit items, but in this product the quota strip is equivalent to Git branch or Problems: it is operational state.

### Popovers

Popovers should be short, not mini dashboards. The token popover is acceptable because it is a drill-down from the statusbar, but it should keep the first row focused: current quota, window, reset, and route suggestion. Deeper charts belong in Token Usage or Verbose Debug.

## Best Practice Comparison

| Reference | Useful rule | Prototype implication |
|-----------|-------------|-----------------------|
| VS Code Workbench | Use stable containers: Activity Bar, Side Bar, Editor Group, Panel, Status Bar. | Model the app as a workbench, not as one page with many cards. |
| VS Code Activity Bar | Activity items represent View Containers with clear names and product-style icons. | Treat Queue, Search, Git, QA, Tokens, and Projects as modules that can be focused or hidden. |
| VS Code Sidebars | Group related Views, avoid too many View Containers, and keep View counts modest. | Queue is optional Side Bar context. It is not permanent chrome. |
| VS Code Editor Groups | Opened files and custom editors live as tabs in the editor area and can be split or closed. | Treat Summary, Task Chat, Git, Screenshots, and Debug as task documents rather than permanent panels. |
| VS Code Panel | Use panels for supporting Views that benefit from horizontal space and can be minimized. | Verbose Debug and long trace/test output belong in supporting panels or fullscreen debug, not in the default transcript. |
| VS Code Editor Actions | Contextual icon actions only; secondary actions go to overflow. | Pane headers stay sparse. Rare controls move to menus or the command palette. |
| VS Code Quick Picks | Use for selecting, filtering, and short multi-step inputs. | Model, owner/project, artifact jump, and target scope should become compact pickers. |
| VS Code Status Bar | Short labels, limited items, global left, contextual right. | Keep statusbar tight. Codex/Claude quota is the deliberate exception because it is operational state. |
| VS Code Views | Prefer existing containers, descriptive labels, product icons, few actions. | Pane controls belong in rails and headers. Avoid turning every row into a command button. |
| VS Code Webviews | Only use custom surfaces when necessary; keep them themeable and accessible. | Angular panes are allowed, but every pane must still obey theme tokens, keyboard access, and scoped actions. |
| VS Code Webview UI Toolkit | Themeability and accessibility are the useful ideas; the package is deprecated. | Do not add the dependency. Use a small internal primitive set instead. |
| Claude Code | Compact session controls and usage commands matter. | Keep usage/model/permissions/start-stop reachable from composer/statusbar. |
| Codex | Task artifacts, screenshots, code review, and automation are first-class. | Keep Git, screenshots, debug trace, and result evidence adjacent to chat. |
| GitHub Copilot Chat in VS Code | Chat is integrated into the workbench, not isolated from editor context. | The task chat should sit beside Git/source/result panes and remain closable. |

## Review Loop

Use this loop for every visual iteration:

1. Pick one component boundary and state its production role.
2. Capture screenshots for default, compact, dark, mobile, and the component's active popover or modal.
3. Check metrics: height, width, padding, density, visible counts, and overflow behavior.
4. Check values: are Codex/Claude quota, tokens, state, run, Git, visual evidence, and owner/project data meaningful?
5. Check sense: is this global, project-level, task-level, run-level, or message-level?
6. Check VS Code alignment: navigation in rails, work in the center, status at the bottom, rare commands in command/menu surfaces.
7. Check text: no technical raw output by default; technical layer is reachable through details/debug.
8. Check theme: light first, dark parity, no contrast regressions.
9. Write a short finding for each failing component before changing CSS.
10. Apply one targeted change, rebuild, rerun Playwright, compare screenshots, then commit.

## Next Extraction Order

1. `ActivityRail`: move activity items and rail styling out of the host.
2. `TaskQueueList`: separate queue cards and lane metadata from the host.
3. `WorkbenchPaneHost`: isolate chat/result/git/preview/debug pane toggling and splitter behavior.
4. `StatusPopover`: move token, queue, health, evidence, session, project, and model popovers out of the host.
5. `ConversationTranscript`: move actor and decision rendering into a reusable renderer.
6. `ComposerBar`: make model/defaults/start/stop/configuration a reusable control surface.
7. `VerboseDebugModal`: separate developer diagnostics from normal conversation.

The goal is a reference that can be implemented incrementally in the real app. Each extracted component should keep the same test ids until the production integration has its own stable selectors.
