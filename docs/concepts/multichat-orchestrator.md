# Multichat orchestrator — context-bound sessions with own history

**Status:** concept 2026-07-08, deliverable of **AGT-1917**. Companion:
[`remote-execution-product-integration.md`](remote-execution-product-integration.md)
(§7 places this as MVP priority 2), AGT-1915 (iframe split view),
AGT-1916 (context header — merged, built data-only *for this surface*).
Mockup: [`mockups/multichat-orchestrator.html`](mockups/multichat-orchestrator.html).

## 1. The shift

Away from ONE global orchestrator history ("everything that ever happened")
to **context-bound orchestrator instances**: navigate to a task → get an
orchestrator that carries *that task's* context and keeps its **own
persistent history**; re-enter any time. Many such instances exist in
parallel (10–30), switchable at a glance. The global view survives as one
context among many — no longer the default.

## 2. Session model

**Context key** (string, canonical): `global` | `project:<PROJ-ID>` |
`task:<PROJ-ID>/<TASK-KEY>`. One `OrchestratorSession` per context key.

| Property | Decision |
|---|---|
| Creation | lazy — first open of a context creates the session record |
| Identity | context key + persisted CLI session id (resume token) |
| History | per-context, persistent, append-only |
| Default context | derived from navigation: task page → task context; board with active project → project context; explicit pin overrides |

**Persistence.** Task-scoped history data-side already exists (the
conversation projection reads `logs/cli-output.log` per task). The
orchestrator chat history generalizes the existing persisted
global-orchestrator session into a **session registry**:
`<TaskRepository>/.metadata/orchestrator-sessions/<encoded-context-key>/`
holding `session.json` (CLI session id, model, timestamps, counters) +
`history.jsonl` (chat turns). Task-context sessions archive together with
their task (same lifecycle, same git-backed evidence trail).

## 3. Lifecycle & resources — 30 chats ≠ 30 processes

The critical distinction: a **session** (history + resume token, cheap,
persistent) vs. an **active process** (a running CLI turn, expensive).

- A context's CLI process exists **only while a turn is being processed**.
  Afterwards the session is *parked*: process gone, history + session id
  remain. Re-entry resumes via the CLI's native session-resume — no context
  replay cost beyond what the CLI itself persists.
- **Active-process cap** (config, default 3–5): more parallel turn requests
  queue visibly ("waiting for a slot") instead of forking 30 CLIs. The cap
  is the resource story; 30 *parked* sessions cost only disk.
- Token budget: each resume continues an existing CLI session (no re-prompt
  of full history). The per-context token rollup (existing
  `OrchestratorTokenUsage`) moves into the session record → the switcher
  can show cost per context.

## 4. UI

Host stays the **orchestrator side sheet** (push layout contract unchanged).
Three principles (sharpened by operator feedback 2026-07-08):

1. **Context is always automatic.** It derives from where the user is
   (task page → task context; project board → project context). There is
   **no "create context" affordance anywhere** — sessions come into being
   lazily on first open of a place. A pin toggle freezes the sheet on a
   context; that is the only manual control.
2. **The switcher rail is extremely optional.** Default state: collapsed —
   the sheet simply *is* the chat of the current place, full width. A small
   "☰ n aktiv" chip in the header expands the rail on demand (sessions
   grouped Global / Projects / Tasks; badges ● running, ◌ parked, unread,
   token cost). A user who never opens it loses nothing.
3. **Rail rows go both ways.** Clicking the name switches the chat;
   clicking the row's "→" **navigates the app to that place** (task detail
   / project board) — the navigation then pulls the context along anyway.
   The rail is monitoring + jump-off for parallel work, never a required
   step.

**Context header** pinned above the chat: exactly the merged AGT-1916
`OrchestratorContextHeaderComponent` (project · task + lane pill · live-run
telemetry) — reused verbatim, as designed. Works unchanged inside the
AGT-1915 split view (the sheet IS the right-hand pane there).

## 5. Implementation plan (phases)

| Phase | Scope | Touches |
|---|---|---|
| **0 — prerequisite: new chat everywhere** | the sheet already uses `@coding-agent/chat` composer ✓; migrate the remaining legacy chat surface (`features/project-chat`, Slice D virtualised history) onto the same composer; audit for other legacy chat renderings | project-chat feature |
| **1 — session registry (backend)** | generalize the persisted global session to context-keyed registry + endpoints `GET/POST /api/orchestrator/sessions?context=…` (list, get-or-create, post turn, park); per-context history store; active-process cap + queue | OrchestratorSession service, new endpoints, `.metadata` store |
| **2 — task-context chat** | sheet resolves context from navigation, auto-switch + pin; context header wired (done data-side); per-context history rendering | orchestrator-side-sheet, panel-state service |
| **3 — multichat switcher** | switcher rail, badges (running/parked/unread/cost), search; park/resume UX | side sheet (splitting its 3-tab 1321-LOC component becomes due here) |
| **4 — placement & gate** | workspace-level admin tabs get the orchestrator settings; feature gate state (see remote-integration §6.4) | settings surface |

Phase 0+1 are independent and can start immediately; 2 needs 1; 3 needs 2.

## 6. Open questions (deliberately deferred)

- Cross-context actions ("summarize all running chats") — later, needs the
  registry first.
- Retention: history.jsonl growth for long-lived project contexts →
  rollup/compaction policy once real sizes are known.
- Whether project-context sessions should see their tasks' session titles
  (linkage only, not merged history) — decide with real usage.
