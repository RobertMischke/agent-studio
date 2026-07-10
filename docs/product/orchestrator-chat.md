# Persistent Orchestrator Chat

This document describes the product and architecture target for an optional, always-available chat window with the orchestrator.

The feature is not "another task chat". It is the user's standing relationship with the system that controls the board: what happened, why decisions were made, what the application knows about each project, and what should happen next.

For the redesign handoff, roadmap slices, and UI contract, see [Orchestrator Chat Redesign Handoff](./orchestrator-chat-redesign-handoff.md).

## Research Notes

Current coding-agent systems keep continuity through a few repeating patterns:

- **Saved local conversations.** Claude Code saves conversations locally and supports `claude --continue`, `claude --resume`, and in-session `/resume` so a task can span multiple sittings. See [Claude Code common workflows](https://code.claude.com/docs/en/common-workflows#resume-previous-conversations).
- **Explicit resume, continue, and fork APIs.** Claude's Agent SDK exposes continue, resume, and fork semantics as first-class session options. See [Claude Code Agent SDK sessions](https://code.claude.com/docs/en/agent-sdk/sessions).
- **Background-agent dashboards.** Cursor Background Agents expose a sidebar where users can view agents, send follow-ups, inspect status, and take over. Cursor also documents that background agents require data retention for a few days. See [Cursor Background Agents](https://docs.cursor.com/en/background-agents).
- **Chat history and past-chat references.** Cursor stores regular agent chat history locally in SQLite and lets users open previous chats or reference past chats as context. Background-agent chats live separately in a remote store. See [Cursor chat history](https://docs.cursor.com/agent/chat/history).
- **Programmatic session resumption.** Codex has an interactive resume path and a headless `codex exec resume` path. Open issues around headless forking show the same design pressure: orchestration surfaces need resume and fork without driving a TUI. See [openai/codex issue 11750](https://github.com/openai/codex/issues/11750).

The pattern is clear: the user's feeling that a session is "still open" does not require a process to stay alive forever. It requires a stable session identity, durable transcript, resumable execution, visible status, and a compact memory object that can be reloaded after days.

## Product Shape

The app should expose an optional **Orchestrator Chat** window that can be pinned, hidden, or opened from a project or global board context.

The chat has two scopes:

- **Global orchestrator.** Answers cross-project questions: what is happening across the board, which projects need attention, whether the queue is healthy, and what should be reviewed first.
- **Project orchestrator.** Answers project-local questions: what this application does, what jobs ran recently, what decisions were made, what the roadmap says, which tasks are blocked, and what follow-up should be created.

The user should be able to tell which scope is active. A target selector is better than pretending one model sees everything at once.

## Architecture Answer

The user-facing chat should use the same canonical orchestrator session that owns the scope:

- A board-level chat talks to the **global orchestrator**.
- A project-level chat talks to that project's **canonical project orchestrator**.
- Task agents remain separate. They execute jobs and report evidence; they do not become the user's standing orchestrator chat.

This means the app does not need two competing project orchestrators. It needs two levels:

1. One global orchestrator for cross-project awareness.
2. One project orchestrator per watched project for project-local memory, decisions, and chat.

That model matches the existing ADR-0007 and ADR-0009 direction. The chat is not a second brain next to the orchestrator. It is the visible conversation surface for the orchestrator that already owns the project or board scope.

## Session Registry Contract

The backend exposes a context-keyed session registry for the canonical
orchestrator scopes. The accepted context keys are:

- `global` for the board-level orchestrator.
- `project:<PROJ-ID>` for one watched project's canonical orchestrator.
- `task:<PROJ-ID>/<KEY>` for a task-scoped orchestrator context.

The registry is lazy: reading a valid context key creates the backing record
when it does not already exist. Records are stored below
`<TaskRepository>/.metadata/orchestrator-sessions/<encoded>/`, where
`session.json` holds the current session metadata and `history.jsonl` is the
append-only sidecar for future session events. The encoded path segment is the
URL-safe form produced by `OrchestratorContextKey`.

The HTTP surface includes registry reads plus asynchronous turn dispatch:

- `GET /api/orchestrator/sessions` lists known sessions and ensures the global
  session exists.
- `GET /api/orchestrator/sessions/{contextKey}` returns or creates one valid
  context-key record. Invalid context keys return `400`.
- `POST /api/orchestrator/sessions/{contextKey}/turns` appends a user prompt
  to the canonical session context and dispatches it through the existing
  orchestrator CLI runner. If `session.json` has a `sessionId`, the runner uses
  the resume path; otherwise it starts a fresh one-shot and stores the captured
  session id.
- `POST /api/orchestrator/sessions/{contextKey}/park` parks queued turns for
  that context and cancels the active turn if one is running.

Turn dispatch is capped by `Orchestrator:SessionTurns:ActiveLimit`, default
`4`. Requests above the cap return `status: "queued"` with a one-based
`queuePosition`; requests admitted immediately return `status: "active"`.
All state changes append compact entries to `history.jsonl`.

The legacy singleton global orchestrator session is migrated into the `global`
context key on first registry access so existing installs keep their board-level
session identity.

## Per-Context Chat Transcript

The side sheet's chat follows the operator's navigation (MC-2, Concept §4): the
board is a `project:<PROJ>` context, an open task page is a `task:<PROJ>/<KEY>`
context, and a pin freezes whichever is current. Each context has its own
transcript so a pinned task and the board no longer share one history.

- `GET /api/runner/{contextKey}/orchestrator-chat` returns the transcript for a
  navigation context. `{contextKey}` is the same canonical key as the session
  registry — `project:<PROJ>` (one path segment) or `task:<PROJ>/<KEY>` (two).
  The response is `{ contextKey, project, turns }`.
- `POST /api/runner/{contextKey}/orchestrator-chat` sends a user message and
  persists both turns to that context's transcript, returning
  `{ contextKey, project, reply }`.

Storage is context-keyed on top of the existing per-project chat store: a
`task` context is written to `<watchPath>/.orchestrator/context-chats/<encoded>.jsonl`
(the reversible `OrchestratorContextKey` encoding), while `project` / `global`
contexts resolve to the canonical `<watchPath>/.orchestrator/orchestrator-chat.jsonl`.
A `project:<PROJ>` request therefore serves the exact same thread as the legacy
`GET /api/runner/{projectName}/orchestrator-chat` route, so the board's chat is
unchanged. The literal-prefixed routes are strictly more specific than the
`{projectName}` route, so routing prefers them without ambiguity — the same
pattern the session-turn endpoints use.

The shared Claude session, prompt building, and usage accounting stay
project-level; the context key only selects which on-disk thread turns land in
and are read back from, so every context still speaks to the one orchestrator
that owns the scope.

The side sheet consumes these routes directly: it derives the context key from
navigation (`contextKey` on `OrchestratorSideSheetComponent`, frozen while
pinned) and reads/sends through `getOrchestratorChatByContext` /
`sendOrchestratorChatByContext`. The reload effect tracks the context key, so
moving between the board and a task in the same project swaps the visible
transcript even though the project is unchanged. When no context key is
derivable it falls back to the per-project route, keeping the board identical.

## Memory Model

The orchestrator's memory should be layered, inspectable, and rebuildable:

| Layer | Purpose | Storage |
|-------|---------|---------|
| Live session id | Lets the CLI resume the same conversation. | Existing orchestrator-session JSON files. |
| Event log | Durable record of decisions, chat turns, follow-ups, overrides, and app actions. | Existing `orchestrator.jsonl` plus chat entries. |
| Working memory | Compact "what I currently know" briefing: project purpose, current roadmap, active risks, recent task results, open decisions, next tasks. | New per-project memory snapshot, for example `.orchestrator/orchestrator-memory.md` or JSON. |
| Source context | Human-owned truth: README, ROADMAP, AGENTS, architecture decisions, skills, project docs. | Existing repository files. |
| Task evidence | Job results, status summaries, logs, screenshots, commits, and review decisions. | Existing job folders and run artifacts. |

The memory snapshot is not a secret system prompt. The user should be able to open it, diff it, refresh it, and ask why something is in it.

## Memory Update Policy

Memory should be maintained by a deterministic pipeline first, then optionally refined by the model:

1. Collect source facts: README, ROADMAP, AGENTS, ADRs, project settings, active jobs, recent completed jobs, orchestrator events, review outcomes.
2. Normalize them into a compact memory document with stable sections: project purpose, current state, recent decisions, open risks, next tasks, known user preferences.
3. Keep source links or file references for every important claim.
4. Ask the LLM to compress or reconcile only where judgment is useful, for example grouping related follow-ups or identifying drift.
5. Write the resulting memory snapshot as an artifact the user can inspect.

This keeps memory from becoming a hidden, self-referential chat summary. The source of truth stays in local evidence.

## Keeping It Alive

The chat should feel alive because the system continuously records what happens, not because it blindly pings the model.

Recommended behavior:

- On app start, resume the saved global and project orchestrator sessions where available.
- When a job changes state, a run finishes, a circuit breaker fires, or the user makes an override, append a structured event to the orchestrator log.
- Periodically, or after meaningful events, update the compact memory snapshot from deterministic sources: job results, roadmap, project settings, recent decisions.
- Only call the LLM when the user asks a question, an auto-mode decision requires it, or a memory refresh needs model judgment.
- If a session id is stale, re-boot from the memory snapshot and event log, then show the reset plainly in the chat.

A heartbeat may be useful for UI status, but it should be a local freshness check. A "keep alive" LLM call that only burns quota is not a product value.

## Context Transparency

The chat window should include a **Context** view. It answers:

- Which orchestrator am I talking to, global or project?
- Which CLI, model, and session id is backing it?
- When was it booted or last resumed?
- What source files were loaded into the initial briefing?
- What memory snapshot is active?
- Which recent jobs and decisions were summarized into memory?
- What is the last known context or token usage where the CLI exposes it?

This makes the orchestrator feel present without becoming mystical. The user can see what it knows and where that knowledge came from.

## Session Events Versus Conversation

CLI session mechanics are evidence, not the primary chat object. A continuation, recovery, steering handoff, or auto-orchestrator intervention should be rendered as a compact timeline event unless it contains semantic content the user needs to read as a message.

Examples of event-bubble content:

- session continued,
- session recovered,
- user steered the agent,
- orchestrator reissued a follow-up,
- memory refreshed,
- auto-loop circuit breaker fired.

The raw event remains expandable and auditable, but the main surface stays a continuous Global or Project conversation. This keeps the user's attention on project intent, decisions, and next steps instead of a registry of transport/session details.

## Control Surface

The orchestrator chat may eventually control the app, but only through typed app actions, not free-form DOM manipulation.

Examples:

- Create a task draft from a chat answer.
- Move a task only through the same state-transition API as the board.
- Start, stop, or continue a task with the same gates the UI uses.
- Refresh project memory.
- Summarize recent job results into roadmap proposals.
- Open a project, job detail, protocol pane, or git view.

Actions that change task state or spend CLI quota should be visible as planned actions before execution unless they are already covered by auto-mode rules.

The chat can still feel powerful without bypassing the application. The orchestrator proposes actions in structured form; the app validates and executes those actions through the same endpoints and policies the UI uses.

## Forking Semantics

The default chat should talk to the canonical orchestrator session for its scope. That gives the user continuity and keeps the session identity stable.

Forks are still useful, but they should be explicit:

- **Speculative fork.** Explore a possible plan without polluting the canonical session.
- **Research fork.** Investigate a large question and return a compact finding.
- **Recovery fork.** Reconstruct context if the canonical session becomes stale.

The product should avoid two peer project orchestrators that both think they own the same project. The architecture is one global orchestrator, one canonical project orchestrator per watched project, and optional short-lived forks that report back.

Fork results should be folded back as evidence, not as a new owner. A fork can produce a research note, a task draft, or a memory update proposal. The canonical orchestrator decides whether to absorb it.

## First Implementation Slice

The smallest valuable slice is:

1. Add a pinned or collapsible Orchestrator Chat panel.
2. Let the user choose Global or current Project scope.
3. Send messages to the existing global or project orchestrator session and append replies to the orchestrator log.
4. Render the same chat after reload from the log.
5. Add a Context view showing session id, model, boot source, last activity, and memory snapshot status.
6. Add a manual "Refresh memory" action that builds or updates the project memory snapshot from deterministic sources first.

That slice gives the user a durable conversation and visible context before the chat starts controlling the rest of the app.

## Open Product Questions

- Should the Orchestrator Chat be a right sidebar, a bottom drawer, or a project-detail tab? The feature wants to be persistent, so a pinned side panel is the likely first shape.
- Should memory refresh be automatic after every completed job, or only after review acceptance? Review acceptance is safer because it means the user agrees the result is real.
- Which actions need confirmation? A good first rule: read-only and draft-creation actions can run immediately; task state changes, CLI starts, stops, and continues need visible confirmation unless auto-mode already owns the decision.
- How much of the raw transcript should be loaded into chat? The default should be summaries plus source links; full logs stay one click away.
