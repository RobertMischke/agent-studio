# Design Principles

The product principles that govern *how* this app feels and *what* it shows. Architectural decisions live in [architecture-decisions.md](../architecture/decisions/adr-archive.md). Product scope lives in [README.md](../../README.md) and [ROADMAP.md](../../ROADMAP.md). This file holds the user-experience contract that ties them together.

## A layer on top of agents and software

This app is an abstraction layer on top of two systems:

1. **The agents** that do the work (Claude Code, Codex, Copilot, Gemini, the orchestrator).
2. **The software** the agents are changing (your repo, its commits, its files, its metrics).

Both layers run with or without us. The agents have their own logs and transcripts; the software has its own git history. What this app provides is a **single surface where you can see both, condensed at the top, and zoom in on demand**.

Two non-negotiables follow from that:

### 1. The software is always visible

You can always answer, *for any task or any moment in a task*:

- Which commits did the agent make on this step?
- What files did it touch?
- What changed in the codebase since the last review point?
- Which metrics moved (test counts, coverage, dependency changes, lint, build time)?

The change in your software is the unit of trust. A run that produced no commits is structurally different from one that produced ten, even if the agent's prose is identical. The UI surfaces that difference at the top level so you don't have to alt-tab into a terminal to find out.

### 2. Drill-down is always available

Every condensed view has a path to the underlying detail:

- A run summary expands to the run's full activity log.
- A run's commit list expands to the diff for each commit.
- An agent message expands to its tool calls.
- A tool burst ("12 reads, 3 edits") expands to the per-call list.
- A heuristic verdict ("Could not classify the agent's reply") expands to *why* the heuristic fired.

You should never see a high-level claim that you cannot interrogate. If we hide something at the top level for legibility, the path to the underlying evidence has to be one click away.

## Skills are action-driven report producers

Specialist Skills and script-backed loops are explicit actions. The user presses a button or the orchestrator asks for a named action: run a security audit, critique screenshots, run backend tests, run end-to-end tests, generate a source map, analyze module organization, or request the next design version. Broad creative, QA, and source-analysis work does not quietly happen everywhere by default.

Each action produces evidence. Human-readable Markdown is allowed and useful, but the app should also ask for a small structured report block with a schema version, status, summary, metrics, findings, and artifact paths. The structured block is an interface, not a wish. If the model or script fails to produce valid JSON, the UI keeps the raw Markdown visible, labels the report as unstructured, and lets the user inspect or turn it into a follow-up task manually.

The button is the contract boundary:

- The user can see what action is being triggered.
- The generated report lands beside the relevant task or project evidence.
- The app parses structured fields only when the contract is satisfied.
- The raw report stays available when parsing fails.
- Follow-up work becomes a normal queued task.

## Analysis reports are first-class product memory

Some actions are not task execution. They are inspections of the system itself: "are we on track?", "what drifted from the roadmap?", "which jobs are stale?", "did the last batch look healthy?", "which docs need sync?", or "what should be split into follow-up tasks?" These analyses may be manual, scheduled, or produced by a meta-cycle.

The output still follows the same evidence rule:

- Markdown is the human-readable artifact and must remain readable on disk.
- Structured JSON is the app contract when the analysis needs filtering, badges, trends, or follow-up automation.
- A failed JSON parse does not hide the report. The UI marks it as unstructured and keeps the Markdown.
- Reports carry scope: workspace, project, task, run, source branch, time window, and triggering action.
- Reports can reference Agent Message Bus records, runtime logs, screenshots, commits, task folders, and other reports, but they should not duplicate raw evidence wholesale.
- A finding that requires implementation becomes a normal queued task.

The UI should give analysis reports their own place at project level. They are not buried inside one task unless the analysis was explicitly task-scoped.

## Drift is a scored project dimension

Drift is the gap between what the project says and what the project does. It can happen between specs and tasks, tasks and jobs, ADRs and source code, README and product behavior, marketing and shipped reality, design references and screenshots, tests and risk areas, or runtime logs and expected behavior.

The project page should treat Drift as its own destination, not only as a filter inside Analysis Reports. A user should be able to trigger a Drift analysis, see a score, understand which dimensions contributed to it, and create normal follow-up tasks from the findings.

Drift scores are triage, not authority:

- Every score must link to evidence.
- Every dimension must show confidence and source coverage.
- A failed JSON parse must leave the Markdown report visible.
- A drift finding can suggest a task, patch, or documentation update, but it must not silently edit project state.
- The user must be able to see whether a drift item is new, accepted, ignored, already tracked, or resolved.

Architecture drift needs a visual scan surface. A project may define a compact high-level architecture model with at most ten elements. The Drift view can render those elements as a marble-style architecture map: each element has a role, guidelines, allowed dependencies, source refs, and a current drift score. The user should be able to click from a red or yellow element directly into the source files, ADRs, schemas, tests, runtime evidence, and follow-up tasks that explain the score.

## Agent-facing steering context is visible

The instructions that shape agent behavior are part of the product experience. README files, AGENTS files, task contracts, runtime prompts, project settings, skills lookup sections, ADR indexes, and project-specific steering notes are not just repository plumbing. They explain why agents keep making certain choices.

The project page should therefore expose two layers:

- The raw technical documents, linked to their repository location and current revision.
- A shorter human summary that explains what agents are currently told, which rules matter most, which documents look stale or contradictory, and which recent failures suggest a documentation or process change.

When a meta-analysis finds a recurring failure pattern across jobs, the UI should connect the dots: evidence first, then the suspected steering gap, then the proposed README, AGENTS, skill, prompt, task-contract, or process update. The user can inspect the raw reports and create a normal follow-up task. The app should not silently change steering documents behind the user's back.

## Inline meta: explain decisions next to the lever

Settings, toggles, mode pickers, and configuration surfaces are not bare controls. Every controllable behavior the platform exposes — agent permission modes, sandbox / YOLO toggles, auto-commit/push strategy, review thresholds, drift rules, skill activation, watchdog timing, model routing — must carry its meta-context *in the surface itself*:

- **What this setting does** in one short sentence.
- **What we default to and why** — the decision basis, not just the value.
- **Risk rating** when the setting trades safety for throughput (e.g. YOLO mode).
- **How to verify** the setting is actually in effect (a probe URL, a CLI command, a log line to grep).
- **Link to the deeper doc** as a drill-down, never as the only source.

The product's value comes partly from the patterns and decisions it has *already made* on the user's behalf. Hiding those decisions in `docs/` or a wiki forfeits that value. The user — and a future agent reading the surface — should be able to pause on any control and read, in-line, why it exists and how to think about flipping it.

Two implementation rules follow:

1. **One source, two views.** The deep explanation lives in a Markdown doc under `docs/` (e.g. `docs/cli/skills/sandbox-and-yolo.md`). The UI embeds the short version inline (or even renders the relevant Markdown section in-place). When the doc changes, the UI updates without copy-paste drift.
2. **Decision rationale, not just behavior.** A help blurb that only says "Enables danger-full-access" is incomplete. It must also say "We default this on because the orchestrated runner gets blocked by interactive sandbox prompts that have no human in the loop; the risk is X; if you don't want this, do Y."

A setting without inline meta is a regression against this principle and should be fixed before shipping.

## See What Happened With Confidence

The user must always have a confident, current picture of what the agents and the software did. Three rules follow:

- **No stale state.** A banner that claims "Agent is mid-task" must reflect the *current* run, not a previous one. When the truth changes, the surface updates within the same render frame; we never let an old signal linger.
- **Show errors plainly.** If a run errored, say so in the spot the user is looking at. Hidden failures, silent fallbacks, or "everything is fine" states draped over a real failure are worse than a red banner.
- **One signal per fact.** A failed run produces one explanation, not three. The orchestrator's deterministic decision messages, the system error, and the heuristic fallback should not all narrate the same event redundantly.

## Chat is a multi-actor conversation

The task chat is not a simple user/assistant transcript. It is a project conversation where several actors can speak:

- The user provides intent, steering, interruption, and review decisions.
- The task agent performs the implementation work.
- The orchestrator interprets outcome, reissues work, answers bounded needs-input loops, and explains deterministic policy decisions.
- The supervisor adds advisory health and risk signals.
- Supporting agents produce explicit QA, security, design, drift, and meta-analysis reports.
- Tool runners create low-level evidence such as reads, searches, edits, shell commands, browser runs, tests, and screenshots.
- System rows explain parser warnings, missing structured output, or artifact handling.

The UI must make those actors recognizable without relying on color alone. Labels, compact avatars or rails, role chips, and typed inline rows are part of the evidence model.

Tool calls are especially dense. Conversation mode should collapse contiguous tool activity into compact tool bursts with counts, failures, duration, touched files, and artifact links. Trace mode keeps every raw entry available. A failed tool burst must show its failure count even while collapsed.

Orchestrator and supervisor output should render as terse decision or advisory rows in the normal chatflow, not as ordinary model prose and not as oversized dashboard cards. Expanded details answer: what was decided, why, which evidence was used, what action follows, and which budget or retry limit applies.

The meta layer belongs beside or behind the transcript. Metrics, run timelines, tokens, screenshots, commits, tests, and raw trace filters can dock into an inspector when there is room, but the central column remains the compact chat.

Task starts, continuations, and task boundaries are real events, but they should not dominate the default transcript. They appear as subtle timeline markers inside the continuous project chat. Hover or click exposes job id, lane, model, prompt, duration, tokens, commits, and evidence.

Chat surfaces must stay embedded in the existing application. Task-scoped conversation belongs in the task detail Chat tab. Project-scoped conversation belongs in the resizable side sheet. Do not introduce a new global chat window to solve a layout problem that belongs in those two surfaces.

Light theme is a first-class product surface. Dark theme must use the same component grammar, spacing, actor labels, warning signals, and debug affordances, but light mode is not a secondary skin.

The chat redesign must preserve the existing product functions while it changes their presentation. The Activity Log parser, Trace mode, run timeline, auto-eval banner, task composer modes, reusable project chat component, CLI Usage sheet, Status Bar quota, Workspace Token Timeline, and project token summaries are inputs to the next design, not clutter to discard. A mockup can simplify them visually; implementation must either keep them reachable or provide a tested replacement.

The first, dashboard-like diagnostic surface remains valuable as a separate `Verbose Debug` view. It is a read-only fullscreen developer view for understanding history and causality: actor activity counts, orchestrator actions, supervisor advisories, duration, tool density, warnings, task markers, artifacts, token usage, and raw trace links. It must not replace the compact human chat.

The next-generation chat mockup lives at [mockups/chat-window-next-gen](../mockups/chat-window-next-gen/README.md).

## The orchestrator has visible memory

The orchestrator should feel present because it can explain what it knows, not because the UI pretends a hidden model is always awake.

The user must be able to answer:

- Which orchestrator am I talking to: global or project?
- Which session id, model, and CLI back this conversation?
- What was loaded when the orchestrator booted?
- Which job results, decisions, roadmap items, and open tasks are currently in memory?
- When was that memory refreshed?
- Which app action is the orchestrator proposing or taking?

Durable memory is a product surface. It should be visible, refreshable, and rebuildable from local evidence. The memory snapshot may be compact, but it must not be mysterious.

## Continuous over batch

The agent runs continuously; so does our view of it. We summarize as we go, not just at the end:

- Run summaries update during the run, not only after it exits. A long-running run still gives the user a current condensed view.
- Software-side aggregations (commits made, files touched, tests run) update at run end and refresh on the next read; they are not gated behind a separate user action.
- The session-level overview ("3 runs, 12 commits, last activity 5 min ago") follows from the run-level data and is never edited by hand.

## A run is the unit of conversation

A *run* is one CLI invocation between two user inputs. A *session* is the ordered list of runs that make up a task's work. Inside a run there are turns, tool calls, and orchestrator decisions; across runs there is a story.

This shapes the file layout, the API, and the UI:

- Per-run artifacts (summary, log slice, commit set) live in `runs/run-NNN/` under the job folder. They are append-only, never rewritten across runs.
- A session-level index aggregates runs into the high-level view (`runs/index.json`).
- The UI's protocol pane is a vertical stack of run cards, each collapsed to its summary, expandable to its log + commits.

The runs file layout is documented in [filesystem-contract.md](../contracts/filesystem.md). When the runtime changes, the contract moves with it.

## Why these principles

The bet is that *humans are good at scanning condensed information and bad at scanning raw transcripts*. An agent run can be tens of thousands of tokens. A human reviewer needs the equivalent of a changelog entry, a git log, and a "click here for evidence" link. This document is the rule that keeps us from drifting back to a single big log file with no top-level surface.

When you propose a UI change or a backend service that touches the protocol, the activity log, or the run lifecycle, this file is the bar:

1. Does the top level still answer "what did the agent change in my software?"
2. Is the underlying detail one click away?
3. Are we adding a new signal that duplicates an existing one?

If the answer to (1) or (2) is no, redesign. If (3) is yes, suppress the new signal or replace the existing one. Never stack them.

## Density and chrome

The chat is the content; everything around it is chrome. The user's cost when chrome grows is real and continuous: every extra row of headers pushes the conversation further from the top of the viewport, and on a long task the user pays that cost on every scroll-to-top. The bar is VS Code, which has spent twenty years tuning the editor-versus-chrome ratio for the same shape of work.

The rules:

- **Chrome above the chat reads in tens of pixels, not hundreds.** Title bar 30 px. Tab bar 30 px. Per-pane header 28 px. Padding 6 px / 12 px. Borders are 1 px hairlines, never decorative frames.
- **Persistent navigation lives at the edges, not above the content.** Project switcher, owner filter, runs counter, model select, and dev tools belong in an activity bar (left rail) or a status bar (bottom strip), not stacked on top of the conversation.
- **Meta information is opt-in.** The user opted into a chat-first reading mode by opening the task. Telemetry chips, session ids, token totals, and rate-limit windows are valuable evidence but stay collapsed by default; an "i" affordance reveals them on demand. Reflexively showing them in the header is a regression.
- **Tabs replace breadcrumbs once two tasks are open.** A 30-px tab strip with kind icon + slug + close button beats a stack of breadcrumb rows; it is also how every code editor handles the same problem.
- **Persisted layout state survives reloads.** Pane widths, side-bar widths, meta-pane open state, and the active tab are all in `localStorage` because the user expects their workspace to look the same when they come back.

The first slice ships behind the `Frontend:VsCodeLayout` feature flag, default off; the spec is [mockups/vscode-layout/](../mockups/vscode-layout/README.md) and the per-element migration map is [mockups/vscode-layout/taxonomy.md](../mockups/vscode-layout/taxonomy.md). When you add a new chrome element, check both: (a) does it survive the density rule, and (b) is there already a taxonomy entry for the destination it belongs in.

## Workbench design system

The product needs its own compact workbench design system. It should learn from VS Code without becoming a clone of VS Code internals.

The rule:

- Use VS Code's public UX guidelines as the primary information-architecture reference: Activity Bar modules, Side Bar views, workbench documents, supporting panels, Status Bar items, contextual editor actions, command palette, and quick-pick style flows.
- Use Code-OSS as a source-code reference for measurements, behavior, layout discipline, and theme-token thinking, not as an application framework to copy into this Angular app.
- Prefer Codicons or equivalent local SVG symbols for product icons so small controls read like developer tooling rather than generic dashboard buttons.
- Build production primitives in Angular with owned CSS tokens and Angular CDK / Angular Aria for behavior such as overlays, focus management, menus, dialogs, splitters, keyboard interaction, and accessible disclosures.
- Avoid broad dashboard kits as the default visual layer. Angular Material, Fluent UI Web Components, Taiga UI, PrimeNG, NG-ZORRO, or similar kits may be useful for isolated spikes, but they must not override the workbench density and component grammar.
- Keep light theme first-class and dark theme structurally identical. A theme switch changes tokens, not layout or hierarchy.

The first named system is the internal Found Next Workbench Design System, documented for the next chat surface in [mockups/chat-window-next-gen/design-system-options.md](../mockups/chat-window-next-gen/design-system-options.md). When production components graduate from a mockup, their tokens, spacing, icon usage, keyboard behavior, and screenshot evidence should move with them.

## Kanban board

The project Kanban board has a single visual specification. Every layout change to the board must reconcile with it before touching CSS, components, or grid math. The spec lives at [mockups/kanban-board-design/](../mockups/kanban-board-design/README.md), with the locked decisions in [mockups/kanban-board-design/taxonomy.md](../mockups/kanban-board-design/taxonomy.md), the interactive reference at [mockups/kanban-board-design/ui.html](../mockups/kanban-board-design/ui.html), and the reconciliation table for the in-flight layout tasks at [mockups/kanban-board-design/reconciliation.md](../mockups/kanban-board-design/reconciliation.md). The first-slice CSS lands behind `Frontend:KanbanDesignSpecV1`, default off.

The non-negotiables that govern board work:

- The lane row uses `grid-template-columns: repeat(N, minmax(220px, 1fr))`. `N` is the count of currently visible lanes.
- Lane headers are 36 px tall, 13 px chrome text, never carry a background fill. Phase color shows as a 1 px outline tint.
- Cards are 56-200 px, 6 px radius, 10 px padding, `--surface-2` background. Selection uses a 2 px `--accent` outline; the brightness never changes.
- Spacing is on the 4 / 8 / 12 / 16 px scale. The single recorded amendment is the 10 px card padding (carry-over).
- Drag uses `transform` and `opacity` only. Drop animates over 180 ms; lane reorder over 200 ms. Cards never transition `background-color`.
- Per-project collapse persistence (`atp.kanban.collapsed.<project>`); `7-archive` is collapsed by default for new projects.

When you add a new lane, change a column width, or adjust the card stack, check both: (a) does the change survive the locked rules above, and (b) does it require an amendment in the taxonomy. A change without an amendment is a regression.

## Motion

Motion has to feel correct, not just be technically present. The rules below come out of repeated regressions where transient overlays read as a "flash" because the user's eye picked up the lift and the snap-off as two distinct events.

- **Drag-and-drop never changes brightness on the card or its column.** Only `opacity`, `transform`, and (for hover depth) `box-shadow` may animate during drag-and-drop. `background`, `background-color`, and `filter` are off-limits; transient overlays that snap off on drop register as a flash.
- **Drop-zone activation fades in via `opacity 0 -> 1`** over ~120 ms, not via a `background` transition that ramps a colour. Glows that leak past the strip's bounds via `box-shadow` are equivalent to a colour ramp and are not allowed.
- **Drop is optimistic.** The new position is in the DOM within one animation frame of the drop event. The reorder POST is fire-and-forget; the layer that pins this lives in `JobService` (`pendingPersistCount`, `applyOptimisticReorder`, `applyOptimisticMove`). The user-visible card never round-trips through the server before settling.
- **Sibling reflow uses `transform`.** When a card lands and the rest of the column has to make space, the rhythm is `transition: transform 180ms cubic-bezier(0, 0, 0.2, 1)` (compositor-only, GPU-friendly). `top` / `margin` would trigger layout and break the rhythm.
- **The drag source eases its opacity restore on release.** The browser's native drag handling drops the source to ~50% opacity, then snaps back to 100% on drop. We tame the snap with a controlled class (`app-job-card.drag-source`) so the source eases back to full opacity instead of popping.
- **Reduced-motion is honoured.** Under `@media (prefers-reduced-motion: reduce)` the card's transition list, the drop-zone bar's transition, and the drag-source restore all collapse to zero duration. The optimistic state change still applies; only the easing disappears.
- **Reconcile without re-rendering the lane.** When the server confirms a move, patch the affected rows in place. Re-setting the entire `grouped` snapshot collapses transitions because the DOM nodes are recreated.

The contract is pinned by `frontend/e2e/dnd-no-flash.spec.ts`. When you change drag-and-drop styling, run that spec and add an assertion if your change introduces a new transition surface.
