# Orchestrator Chat Redesign Handoff

This handoff turns the current Orchestrator Chat concept into a product and implementation brief for the redesign.

The north star is a continuous project conversation with strong search and visible context. The user should feel that the project has one durable chat partner that knows the board, the jobs, the project docs, the recent decisions, and the current risks. Technical session mechanics remain inspectable, but they stop dominating the surface.

For a clickable dummy prototype of the system map and folder-driven drilldown, open [Orchestrator System Visual Report](../reports/html/orchestrator-system-visual-report.html).

## One Sentence

Make Orchestrator Chat the persistent project and board conversation layer, with search and context as first-class tools, while collapsing CLI session mechanics into compact timeline events.

## Current Problem

The current panel exposes useful raw machinery too prominently. A stack of rows such as "You steered; Orchestrator responded" proves that continuity events are being captured, but it reads like the main content. The user has to mentally translate session events into the actual story:

- what the user asked,
- what the orchestrator understood,
- what the task agent did,
- which files, commits, screenshots, and decisions matter,
- what should happen next.

That translation should be the product's job.

## Product Goal

The redesigned chat should answer three questions quickly:

1. What is happening in this project?
2. What does the orchestrator know, and where did that context come from?
3. How do I search or act on prior tasks, commits, files, screenshots, chat turns, and decisions?

The target feeling is not "a list of sessions". The target feeling is "the project is still in conversation with me".

## Scope Model

The redesign keeps the existing canonical-session architecture:

| Scope | Primary role | Backing session |
|-------|--------------|-----------------|
| Global chat | Cross-project status, queue health, priority, and routing. | The global orchestrator session. |
| Project chat | Project-local continuity, task history, roadmap, decisions, and follow-ups. | The canonical project orchestrator session. |
| Task activity | Evidence for one task or run. | Task agent session ids, run timeline, activity log, status files, commits, and artifacts. |

The user can switch between Global and Project scope. The default entry point from a project should be the Project chat because the strongest use case is continuous context on one codebase.

## Information Architecture

The redesigned panel should separate human conversation from machine evidence:

| Area | Purpose | Notes |
|------|---------|-------|
| Conversation | User and orchestrator messages. | The primary scroll surface. |
| Event bubbles | Compact system events: session continued, recovery, auto-loop decision, task created, memory refreshed, source reindexed. | Small, expandable, visually quieter than messages. |
| Context drawer | What the orchestrator knows and what evidence fed that memory. | Session id, model, boot source, memory snapshot, recent jobs, source files. |
| Search | Search across chat, tasks, commits, files, screenshots, and orchestrator decisions. | Results should be filterable and insertable into the chat as context references. |
| Actions | Typed app actions proposed by the orchestrator or invoked by the user. | Draft-first for risky actions. State changes and quota-spending actions stay auditable. |

## Session Events Become Bubbles

Rows such as "You steered; Orchestrator responded" should become compact event bubbles inside the conversation timeline.

Recommended behavior:

- Default collapsed label: "Steered agent, orchestrator replied" or "Session recovered".
- Bubble metadata: time, run count, session id chain, mode (`continue`, `steer`, `extend`, `newTask`), linked task.
- Expand details on click: original technical event, session ids, recovery reason, related log lines, raw JSON if needed.
- Keep raw evidence in the existing log and event store. The redesign hides visual noise, not auditability.
- Promote only meaningful semantic content into full message bubbles: user request, orchestrator answer, agent answer, decision, warning, or action proposal.

This is not only cosmetic. It preserves the mental model: sessions are transport and continuity evidence; the project chat is the product surface.

## Search And Context Layer

Search should be a core capability of Orchestrator Chat, not a separate utility bolted onto the header.

Search targets:

- chat turns,
- orchestrator decisions,
- task titles and prompts,
- `status.md` review protocols,
- CLI activity logs and parsed turns,
- commits and changed files,
- screenshots and result artifacts,
- README, ROADMAP, AGENTS, ADRs, and project docs,
- current memory snapshots.

Expected result behavior:

- Filter by scope: global, project, current task, archived tasks.
- Filter by type: chat, task, commit, file, screenshot, decision, doc.
- Show why the result matched.
- Open the source in place.
- Let the user attach one or more results to the next chat message as explicit context.
- Let the orchestrator cite results when it answers.

The first implementation can be local and pragmatic: index structured task metadata, Markdown files, event logs, and commit metadata. Full semantic search can come later, but the UI should already behave like search is a first-class project memory tool.

## Context Contract

The Context drawer answers what the orchestrator is relying on right now.

Minimum fields:

- active scope,
- project path and display name,
- backing CLI and model,
- session id and last activity,
- boot source files and timestamps,
- memory snapshot version and freshness,
- recent jobs summarized into memory,
- open risks and next tasks,
- last indexing time,
- last memory refresh result,
- token or quota information where the CLI exposes it.

The memory snapshot must stay inspectable and rebuildable. It should not become a hidden prompt that silently overwrites repo truth.

## Main User Flows

### Ask About The Project

The user asks, "What is going on with this project?" The project orchestrator answers from memory and cites recent tasks, status files, commits, roadmap items, and decisions.

### Find Previous Work

The user searches for a term, sees tasks, commits, screenshots, and chat decisions, opens the best result, and attaches it to a follow-up question.

### Continue A Task

The user writes a follow-up in the project chat and chooses whether it becomes a task continuation, a new task draft, or a roadmap note. The app records the typed action and renders any technical session continuation as an event bubble.

### Explain Hidden Work

The user expands an event bubble to see why the orchestrator reissued a prompt, recovered a session, refreshed memory, or stopped an auto-loop.

### Convert Conversation To Work

The user asks the orchestrator to make a task from a message or reply. The app creates a draft first, then lets the user place it in the queue.

## Roadmap

### Phase 0: Align The Contract

- Add this handoff as the redesign source.
- Add ADR-0012: session mechanics are timeline events, not primary chat objects.
- Update the existing Orchestrator Chat concept doc and roadmap links.

Exit criteria:

- Future contributors can tell the difference between conversation, event, memory, and raw log.

### Phase 1: Conversation-First UI Shell

- Redesign the panel around one primary conversation timeline.
- Add compact event bubbles for session events and auto-orchestrator decisions.
- Keep raw session details behind expansion.
- Keep the current scope selector, but make Project chat the project-entry default.
- Add empty, loading, stale-session, and no-memory states.

Exit criteria:

- The panel no longer looks like a session registry.
- The user can still audit every hidden event with one click.

### Phase 2: Context Drawer

- Add the inspectable Context drawer.
- Show session id, model, boot source, memory freshness, and recent summarized evidence.
- Add a manual Refresh memory action.
- Store memory refresh events as event bubbles.

Exit criteria:

- The user can explain what the orchestrator knows without reading backend logs.

### Phase 3: Project Search

- Build a project search endpoint over task metadata, Markdown protocols, orchestrator event logs, docs, and git metadata.
- Add filters for type and scope.
- Render results in the chat panel.
- Allow selected search results to be attached to a chat message.

Exit criteria:

- Search can answer "where did we discuss this?", "which task changed this file?", and "show the screenshot from that bug".

### Phase 4: Typed Actions

- Add action proposal cards for create task draft, open task, open file or commit, refresh memory, summarize results, and continue a task.
- Require confirmation for state changes, CLI starts or stops, and quota-spending actions outside existing auto-mode rules.
- Record action requests and outcomes in the event log.

Exit criteria:

- The chat can control the app without bypassing backend policy.

### Phase 5: Forks And Research Notes

- Add explicit research forks for large questions.
- Store fork results as evidence or task drafts.
- Let the canonical orchestrator absorb a compact result instead of replacing the canonical session.

Exit criteria:

- Deep research can happen without polluting the continuous project conversation.

## ADR Map

Existing decisions that stay load-bearing:

- [ADR-0002](../architecture/decisions/adr-archive.md#adr-0002---deterministic-orchestration-over-prompt-trust-2026-05-02): orchestration decisions are deterministic and visible.
- [ADR-0007](../architecture/decisions/adr-archive.md#adr-0007---per-project-long-lived-orchestrator-session-for-warm-context-2026-05-02): each project has one resumable orchestrator session.
- [ADR-0009](../architecture/decisions/adr-archive.md#adr-0009---global-orchestrator-above-per-project-orchestrators-2026-05-02): a global orchestrator exists above per-project orchestrators.
- [ADR-0011](../architecture/decisions/adr-archive.md#adr-0011---orchestrator-chat-uses-canonical-scope-sessions-with-visible-memory-2026-05-03): chat uses canonical scope sessions with visible memory.
- ADR-0012: session mechanics are timeline events, not primary chat objects.

Likely future ADRs:

- Search index boundary: local structured search first, semantic search later, no hosted index.
- Chat action safety: draft-first and confirm-before-side-effect rules for typed actions.
- Memory refresh policy: deterministic assembly first, model compression second, user-visible provenance always.

## Data And API Needs

Likely backend surfaces:

- `GET /api/orchestrator/chat?scope=global|project&watchPath=...`
- `POST /api/orchestrator/chat/messages`
- `GET /api/orchestrator/context?scope=...`
- `POST /api/orchestrator/memory/refresh`
- `GET /api/search?scope=project&watchPath=...&q=...&types=...`
- `POST /api/orchestrator/actions/preview`
- `POST /api/orchestrator/actions/execute`

The exact routes can change, but the separation should hold:

- chat transcript,
- technical events,
- context and memory,
- search,
- typed app actions.

## UX Principles

- The primary timeline should be calm and readable.
- Technical events are always present but visually subordinate.
- Context is not magic. It is inspectable, refreshable, and linked to sources.
- Search is part of the conversation loop.
- The orchestrator can propose actions, but the app validates and executes them.
- The default path is continuity, not fragmentation into many sessions.

## Open Questions

- Should event bubbles live inline in the main conversation, in a side rail, or both?
- Should task-specific activity remain inside task detail, while project chat only links to it?
- How much raw CLI output should be searchable by default?
- Should screenshots be indexed by filename and task metadata first, or should image OCR come early?
- Should memory refresh run after review acceptance, after every completed run, or only manually in the first slice?

## Verification Expectations

This redesign is visual and behavioral. Implementation work should include:

- Playwright coverage for the chat timeline, event bubble expansion, context drawer, and search attachment flow.
- Screenshot capture for the main loaded state, collapsed event bubbles, expanded event details, empty state, stale context, and search results.
- Backend tests for event classification, memory snapshot assembly, and search result typing.
- Accessibility checks for keyboard navigation through messages, event bubbles, filters, and action cards.
