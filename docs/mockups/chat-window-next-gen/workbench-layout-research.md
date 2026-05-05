# Chat Workbench Layout Research

This note captures the v7 layout research for the next-generation chat mockup. It focuses on the user's workflow: keep the task chat open while inspecting the result, Git changes, screenshots, tokens, and debug evidence.

## Research Question

Agent Task Processor does not have a global chat window. The chat must work in two existing places:

- The task-detail Chat tab, next to Prompt, Protocol, Files, Commits, and Screenshots.
- The project side sheet, which can become wider and carries project-level steering.

The missing workflow in v6 was side-by-side work. A reviewer often needs the transcript and another surface at the same time: result summary, changed files, screenshots, token budget, or debug metrics. The solution should preserve maximum chat space and avoid a heavy dashboard default.

## Sources

| Source | Useful signal |
|--------|---------------|
| VS Code User Interface, https://code.visualstudio.com/docs/getstarted/userinterface | VS Code maximizes the editor area while keeping context in Primary Side Bar, Secondary Side Bar, Activity Bar, Panel, and Status Bar. It explicitly supports side-by-side editors and a Secondary Side Bar with Chat by default. |
| VS Code Custom Layout, https://code.visualstudio.com/docs/configure/custom-layout | The workbench supports movable side bars, compact Activity Bar, layout toggles, panel positions, panel maximize, drag and drop of views, and editor groups. |
| VS Code Extension UX Guidelines, https://code.visualstudio.com/api/ux-guidelines/overview | Views, view toolbars, sidebars, panels, editor actions, and status bar items are separate containers. This argues for scoped actions and compact toolbar placement. |
| VS Code Views Guidelines, https://code.visualstudio.com/api/ux-guidelines/views | Views can move between containers, but extensions should keep view count and names minimal, use existing icons, and avoid unnecessary custom webviews. |
| VS Code Webviews Guidelines, https://code.visualstudio.com/api/ux-guidelines/webviews | Custom webviews should be used only when necessary, be themeable and accessible, and not repeat existing functionality. |
| VS Code Product Icon Reference, https://code.visualstudio.com/api/references/icons-in-labels | Product icons are the right visual grammar for tiny toolbar actions such as terminal, history, tests, warning, close, refresh, and configuration. |

## VS Code Patterns To Borrow

1. **Editor space is sacred.** VS Code's basic layout is built to maximize the editor while still exposing project context. For Agent Task Processor, the transcript is the equivalent of the editor. It should get the largest continuous region.
2. **Two views at once are a first-class need.** VS Code solves this with editor groups and the Secondary Side Bar. For this app, that maps to Chat plus Result, Chat plus Git, Chat plus Preview, and Chat plus Debug.
3. **Layout flexibility is constrained, not arbitrary.** VS Code has many layout moves, but they are bounded by workbench regions. The first implementation should use a few named split presets, not a full drag-and-drop window manager.
4. **Actions live in toolbars and overflow.** Small icon buttons plus a `...` overflow are preferable to repeated text buttons inside the chat body.
5. **Status belongs at the edge.** Tokens, state, duration, commits, warnings, and screenshots should be visible as small badges or status items. They should not become large inline cards unless opened.
6. **Native surfaces should stay native.** Git changes still belong to Files and Commits, screenshots still belong to Screenshots, and raw output still belongs to Trace. The split pane is a fast adjacent preview, not a replacement.

## User Workflows

| Workflow | What the user wants open | Layout implication |
|----------|--------------------------|--------------------|
| Review a completed task | Chat plus result summary | Summary pane with status, warnings, tests, token total, and final human outcome. |
| Review code changes | Chat plus Git changes | Narrow Git pane with commit count, changed files, line deltas, and links to the existing Files and Commits tabs. |
| Review visual work | Chat plus screenshots | Preview pane with a small evidence reel and lightbox handoff to the existing Screenshots tab. |
| Debug a weird run | Chat plus Debug summary, then Verbose Debug | Small debug pane for quick counts, fullscreen Verbose Debug for causality and raw trace ranges. |
| Continue a task while reading | Chat only or Chat plus Result | Composer stays in the chat host. Side context must not steal typing space. |
| Project steering | Side sheet plus current task context | Project side sheet can widen, but task-specific evidence remains in the task Chat tab. |
| Token triage | Summary badges plus debug pane | Token chip in the strip, token heatmap in Debug, full token surfaces remain elsewhere. |

## Alternative Designs And Critique

### Full Docking Or Window Manager

This is powerful and matches advanced IDE expectations, but it is too expensive for the first implementation. It requires persisted layout state, keyboard focus rules, drag targets, resizing behavior, responsive collapse, accessibility work, and Playwright coverage for many combinations. It also risks burying the chat behind layout mechanics.

Recommendation: do not build this first. Use explicit split presets and revisit a layout framework only after the product proves repeated need.

### Fixed Two-Pane Chat And Inspector

This is easy to implement and predictable. It fails when the user wants a different adjacent surface, such as Git instead of metrics, and it can waste horizontal space on small screens.

Recommendation: keep the two-pane idea, but make the right pane mode-driven.

### Dashboard Header Above Chat

This surfaces state quickly, but it competes with the transcript and can become another operations dashboard. The user specifically wants more chat space, not less.

Recommendation: use a 24 to 32 px summary strip with compact chips. Put details behind the right pane or Verbose Debug.

### Separate Global Chat Window

This contradicts the existing product structure. The app already has task-level chat and project side-sheet chat. A global chat would create routing confusion and duplicate context ownership.

Recommendation: keep the chat embedded in the two existing hosts.

## v7 Recommendation

Build a **task chat workbench** inside the existing task-detail Chat tab:

- Default width allocation: chat gets roughly two thirds, context pane gets roughly one third.
- Layout presets: `Chat`, `Result`, `Git`, `Preview`, `Debug`.
- Summary strip: state, run, tokens, commits, changed files, screenshots, failed retry, duration.
- Tiny toolbar buttons: layout, density, theme, side sheet width, debug.
- The right pane is a preview and drill-down launcher, not a replacement for the existing tabs.
- On narrow screens, collapse to chat-only and route context through buttons or modals.
- Persist the last chosen layout per user and project later, but keep the first mockup static and deterministic.

## Implementation Contract

The queued implementation must preserve these surfaces:

- Existing Protocol/Activity parser and Trace mode.
- Existing run timeline and per-run commit lookup.
- Existing Files, Commits, and Screenshots tabs.
- Existing project side sheet, including project chat, task tab, roadmap intake, attachments, and make-task behavior.
- Existing token surfaces: status bar, CLI usage, workspace token timeline, project summaries, job token chips.
- Verbose Debug as the full read-only developer view.

The first code slice should introduce the split host behind `Frontend:NextGenChat`, not behind the broader app-shell flag. `Frontend:VsCodeLayout` can later make the surrounding chrome denser, but the chat workbench should be useful in the current layout first.

## Open Design Questions

- Should the split ratio be a fixed preset, a draggable divider, or both?
- Should Git preview open the existing Files/Commits tabs in the same task host, or render a local mini-diff preview first?
- Should token chips show total tokens only, or split by task agent, orchestrator, and supporting jobs when the right pane is in Debug mode?
- Should the side sheet remember a separate width for project chat versus task follow-up?
- Which keyboard shortcuts should mirror VS Code split behavior without making the app feel like a clone?
