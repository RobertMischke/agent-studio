# Feedback Queued From A Closed Task

Design proposal for what happens when the user types into the chat of a task
that is no longer in `3-progress`. Anchored against the v5/v7 chat direction:
compact transcript, subtle inline markers, raw technical metadata hidden, no
new global chat window, and the bottom composer stays stable.

This document is a design contract. It does not change production code by
itself; the work is a later queued job once the user has signed off.

## Problem

The current task chat assumes the task is the active subject of work. Today's
composer modes (`Continue`, `Steer`, `Extend`, `New task`) all imply that
hitting Send may immediately start a CLI run. That is correct behaviour for
`3-progress`. It is wrong for tasks that have moved on:

- `4-auto-review`, `5-human-review` - the orchestrator or the user is
  reviewing; a stray Send can re-open a settled outcome.
- `6-completed`, `7-archive` - the task is closed. The user wants to drop a
  note, not relitigate the work.

The user's verbatim ask: comments on closed tasks should be **enqueued** and
visibly marked as "I will deal with this eventually". Pure questions can be
**answered now without code changes**. Real change requests must **wait** for a
proper run rather than mutating the closed task underneath the review lane.

The user explicitly flagged this as tricky and asked for "a cool solution"
first. This is that proposal.

## Design Goals

1. **The composer never lies about what Send does.** In a closed task it must
   be obvious that Send queues a follow-up, it does not restart the task.
2. **Two distinct intents are first-class.** A *question* (no code, no commit,
   no file write) and a *change request* (real work) are different actions
   with different downstream effects.
3. **The transcript stays compact.** A queued comment is one inline marker
   row, not a banner or modal. Verbose details live behind drill-down.
4. **No new global chat surface.** The behaviour ships inside the existing
   task-detail Activity / Chat tab and the project side sheet, behind
   `Frontend:NextGenChat`.
5. **Reversible by design.** Every queued comment can be promoted, edited,
   answered, dismissed, or escalated to a real run by the user.

## Composer Behaviour Per Lane

The set of available modes depends on the lane the task currently sits in.
Mode visibility is computed from `JobInfo.state`; the composer never offers a
mode that would be wrong for the current lane.

| Lane | Modes available | Default | Send behaviour |
|------|-----------------|---------|----------------|
| `3-progress` | Continue, Steer, Extend, New task | Continue | Existing behaviour. Send may pause-and-resume the live run. |
| `4-auto-review` | Ask, Defer, Promote (advanced) | Defer | Send writes a queued comment; orchestrator decides if it answers inline. No source mutation. |
| `5-human-review` | Ask, Defer | Defer | Send writes a queued comment; agent never starts a run from this lane. |
| `6-completed` | Ask, Defer | Defer | Send writes a queued comment. Default marker is "Will be picked up later." |
| `7-archive` | Ask only (read-only chat) | Ask | The composer is muted; Ask runs a sandboxed answer-only call without mutating the task. Defer is hidden. |

The two new modes are intentionally minimal:

- **Ask** - answer-only. The task is treated as read-only. The agent (or the
  orchestrator) may answer inline. No edits, no commits, no test runs, no
  follow-up task is created. Wired internally as a sandboxed read-only
  invocation, similar to a verbose-debug query.
- **Defer** - queue a follow-up. The comment is captured immediately as a
  visible transcript marker. A backing follow-up task is created in the
  project queue (`1-preparation` by default). The current task does not move.
  Picking it up later happens through the normal project pipeline; the
  orchestrator does not auto-start it.

`Promote` is an advanced affordance only available when the user explicitly
wants to move a Defer into the active lane (e.g. trivial typo fix on a closed
task that does not warrant a fresh review cycle). It is not the default and is
not shown without confirmation, because it bypasses the review lanes the
orchestrator is supposed to gate.

## Inline Marker Grammar

Every comment the user posts on a closed task lands in the transcript as a
normal user message. Immediately below it, a single compact row signals the
queue decision. This row is the "marker" the user asked for.

```text
You · 14:02
Could the screenshots also include the dark theme variant?

[ deferred · queued for later · "I'll get to this when there's bandwidth" · open in queue ]
```

Variants:

```text
[ asked · answered inline · no code changes · expand answer ]

[ deferred · created follow-up task #123 · open task ]

[ deferred · merged into existing follow-up #98 · open task ]

[ promoted · started run on closed task · undo within 10s ]
```

Visual rules:

- The marker row is rendered as a single low-emphasis chip-line, same density
  as `decision.orchestrator` and `taskMarker` rows.
- The "I'll get to this when there's bandwidth" label is the default copy for
  the deferred state. It is plain English, not a sentinel string the parser
  cares about. The label is a configuration constant, not user-typed.
- The row carries actions: open queued task, dismiss queue, edit message,
  promote (with confirmation), open in Verbose Debug.
- Raw queue metadata (target lane, target slug, dedupe key, follow-up id,
  orchestrator routing decision) is hidden by default and reachable through
  "expand details" or Trace mode. The compact view never shows JSON.

## Conversation-Event Grammar

The existing conversation grammar already separates user messages from
typed inline rows. This design adds one new event kind plus a small extension
to the user-message contract. Both can be wired without breaking the
exhaustiveness tests in `conversation-projection.spec.ts` because new kinds
are appended.

Proposed (not yet implemented):

```ts
type ConversationEventKind =
  | // ...existing kinds...
  | 'feedback.queued';

interface FeedbackQueuedEvent extends ConversationEventBase {
  kind: 'feedback.queued';
  // Which composer mode produced this row.
  mode: 'ask' | 'defer' | 'promote';
  // Lane the parent task was in when the user pressed Send.
  parentLane: '4-auto-review' | '5-human-review' | '6-completed' | '7-archive';
  // For Defer, the slug of the follow-up task (or null while pending).
  followUpJobId?: string | null;
  // For Ask, true once an inline answer landed.
  answered?: boolean;
  // Short human-readable reason ("queued for later",
  // "merged into follow-up #98", "answered inline").
  label: string;
}
```

`message.user` events get an optional `intent: 'ask' | 'defer' | 'promote'`
hint so the renderer can colour the bubble subtly without coupling to lane
state. The intent is set by the composer at Send time.

## Where The Comment Goes

- **Ask** - the comment + answer are stored in the task's chat transcript
  only. No new files in the job folder. No new task. The orchestrator is
  responsible for refusing to mutate the closed task during this call.
- **Defer** - the composer creates a follow-up task through the existing
  `POST /api/jobs` endpoint with a back-reference to the parent task slug. The
  follow-up's `prompt.md` includes the parent task title, the relevant
  transcript turn, and a one-line hint that the change should be made in the
  parent's domain. The follow-up lands in `1-preparation` by default so the
  project queue logic remains unchanged.
- **Promote** - never silent. Confirmation dialog. On confirm, the parent
  task moves back to `3-progress` and the comment becomes a `Continue`-style
  prompt for the same session. The marker row stays visible in the transcript
  with an `undo` affordance for ~10 seconds.

The deduplication rule for Defer is important: if the user posts three quick
defer comments on the same closed task within a short window (e.g. 30
seconds), the host should fold them into the **same** follow-up task rather
than spawning three. The marker row text changes to "merged into follow-up
#X". This avoids the queue filling with one-line tasks during a review pass.

## Surface Hooks (Existing App)

The proposal lands inside surfaces that already exist; the integration plan
already lists them. No new top-level chat window is introduced.

- Task detail Activity / Chat tab
  (`frontend/src/app/components/job-detail/protocol-pane/protocol-pane.component.ts`):
  the composer mode list becomes lane-aware; `modeOptions` is computed.
- Project side sheet
  (`frontend/src/app/components/orchestrator-side-sheet/orchestrator-side-sheet.component.ts`):
  side-sheet messages on closed tasks reuse the same `feedback.queued` row
  grammar so cross-task steering reads consistently.
- Conversation projection
  (`frontend/src/app/components/chat/conversation-projection.ts`): emits
  `feedback.queued` events when the host adapter detects deferred comments
  in the run timeline / job feed.
- Existing composer surfaces (Continue, Steer, Extend, New task) stay
  untouched. The new modes do not replace them; they add only when the lane
  rules say they should.

## Token, Cost, and Quota Story

- Ask consumes tokens; the inline answer is a real model call. The chip on
  the marker row carries a tiny token count when it is meaningful, the same
  rule the rest of the chat already follows. No dashboard inline.
- Defer consumes essentially no tokens; the follow-up task creation is a
  filesystem write. The marker row carries no token chip.
- Promote consumes tokens at the moment the task re-enters `3-progress`,
  exactly as a normal Continue would. No new accounting needed.

## Resolved Decisions

The points below were carried as Open Questions in the first draft and were
raised again in human review on 2026-05-11. They are decided here so the
implementation job has a contract. The reasoning is preserved next to each
decision so a future change can be made deliberately.

### 1. Manual Ask vs Defer in v1 - no auto-classification

**Decision:** v1 ships with the user explicitly picking Ask or Defer from the
composer mode bar. The product does not auto-classify intent from the comment
text. The default mode for a closed-task chat is `Defer`.

**Why:** Misclassifying a comment as Ask would silently swallow a real change
request; misclassifying it as Defer would create unwanted follow-up tasks
during a review pass. Both failure modes erode trust in the composer and are
hard to debug after the fact.

**Future iteration (not implemented now):** the orchestrator may *suggest* a
mode by highlighting one button based on a heuristic ("starts with a
question word" -> Ask). The user still has to press Send. No silent
auto-submit. This polish is tracked but is not blocking.

### 2. Backend lane-move is required for Promote - `POST /api/jobs/{id}/reopen`

**Decision:** Promote depends on a new backend endpoint that moves a job
from `6-completed` (or `7-archive`) back into `3-progress`. Until the
endpoint lands, the Promote mode is **hidden in the composer**, and the
implementation job must guard the UI on a server capability probe
(`/api/jobs/capabilities` already advertises feature flags). Ask and Defer
ship without the endpoint.

**Endpoint contract (proposed):**

```text
POST /api/jobs/{id}/reopen
Body:  { "watchPath": "<repo-relative path>", "confirmedAt": <iso-8601> }
200:   updated JobInfo with state="3-progress"
409:   { "error": "lane_locked" }   if the lane move is not allowed
```

The endpoint is idempotent on the `confirmedAt` token so a double-click does
not produce two state transitions. The server emits a
`decision.orchestrator` row into the conversation feed so the lane move is
auditable inside the same chat that triggered it.

**Why:** without the endpoint, Promote is a UI lie. The Defer/Ask experience
is the larger feature; gating Promote behind a server flag keeps the v1
slice deliverable without backend coupling.

### 3. Side sheet and task chat share the `feedback.queued` stream

**Decision:** `feedback.queued` events are published on the per-job event
stream the existing job feed already uses (`/api/jobs/{id}/events` SSE).
Both surfaces subscribe by job id and render the same marker row through the
shared `ConversationEventRenderer`. There is no second write path and no
separate side-sheet event kind.

**Implementation note:** the side-sheet task tab already filters the job
event channel; the only addition is that `feedback.queued` becomes a
recognised kind. The host adapter does not duplicate it; it forwards.

**Why:** any second write path would drift. The shared SSE channel is the
contract that already keeps task chat and side sheet aligned for the rest of
the conversation grammar; reusing it is cheaper than maintaining a mirror.

### 4. Ask counts against the same quota window as Continue

**Decision:** Ask calls go through the same CLI invocation path as a
read-only Continue and are billed identically. The CLI usage log adds an
`intent: 'ask' | 'continue' | 'steer' | 'extend'` tag so the existing usage
surfaces (`status-bar`, `cli-usage-sheet`, `usage-hover-panel`,
`workspace-token-timeline`) can attribute and filter without inventing a new
metric. Verbose Debug gets an `intent` chip on the usage panel.

**Why:** quota stealth-drain is a known failure mode for "free" actions.
Tagging the existing usage record is the smallest change that keeps the
existing dashboards correct. A separate quota lane would be a new surface
the proposal explicitly avoids.

### 5. Time-to-pickup label is static in v1

**Decision:** the default Defer marker copy stays `"I'll get to this when
there's bandwidth"`. v1 does not compute or display a queue-depth ETA on
the marker row. The label is a configuration constant in the renderer.

**Why:** an ETA depends on a project queue depth signal that does not exist
yet, and on the actual order the orchestrator picks tasks, which is not
deterministic. Showing a wrong ETA on a closed-task marker is worse than
showing none.

**Future iteration (not implemented now):** when the project queue exposes a
depth signal, the marker row can carry a tiny `queued behind N` chip on
hover. This is a polish task tracked separately and does not change the
composer behaviour.

## Cross-Document Reflection - Scope Note

This proposal is the design contract; the doc lives in
`docs/mockups/chat-window-next-gen/feedback-queued-from-chat.md` and the
scenario row is already cross-linked from `scenarios.md` and `README.md`.

Reflecting the contract into the rollout artefacts
(`host-inventory.md`, `integration-plan.md`, `angular-prototype.md`,
`found-next-ux-review.md`) is **deliberately out of scope** for this design
task. Those documents describe the rollout sequencing, host wiring, and
prototype-status snapshots, and updating them belongs to the implementation
job that actually adds the composer modes and the `feedback.queued` event
kind. Touching them during the design phase would mix design intent with
implementation reality and force the implementation task to re-edit them
anyway.

Captured as a successor follow-up:

- Slug: `chat-feedback-queued-implementation`
- Lane on creation: `1-preparation`
- Required edits:
  - `host-inventory.md`: extend the composer-surface inventory with the
    new lane-aware modes (Ask, Defer, Promote) and list
    `feedback.queued` under the renderer event kinds.
  - `integration-plan.md`: append `chat-feedback-queued` to the
    `Queue Alignment` rollout order, after
    `chat-actor-decision-cards`, with a one-line scope summary.
  - `angular-prototype.md` and `found-next-ux-review.md`: add a single
    cross-link line each to this proposal under the relevant section
    ("Composer Behaviour" and "Closed-Task Chat" respectively); no
    structural changes.
- Acceptance criteria: see the Acceptance Criteria block below; the
  follow-up job carries those forward and reuses this proposal as its
  design contract.

## Acceptance Criteria For The Implementation Job

When this design lands as a coded task, the deliverable must:

- Add the `feedback.queued` event kind, projection support, and tests.
- Make `modeOptions` lane-aware in the task chat composer.
- Render the marker row in both task chat and side sheet (shared grammar).
- Wire Ask as a sandboxed read-only call that cannot mutate the parent task.
- Wire Defer to create a follow-up task in `1-preparation` with parent
  back-reference and within-window dedupe.
- Gate Promote behind a confirmation dialog and emit a `decision.orchestrator`
  row when it fires (so the lane move is auditable in chat).
- Cover Playwright cases: completed-task chat with Defer, archived-task chat
  with Ask muted, dedupe of three quick Defers, Promote with confirm and
  undo, side-sheet defer that mirrors into task-chat.
- Preserve every entry on the host-inventory acceptance gate.

## Non-Goals

- This proposal does not introduce a separate "feedback inbox" surface. The
  marker rows live in the chat where the comment was made.
- It does not change the orchestrator's existing review lanes. Auto-review
  and human-review keep their current semantics.
- It does not introduce intra-project parallelism. Defer comments go through
  the normal project pipeline; the parent task stays sequential.
- It does not change `status.md`, the agent contract, or the sentinel
  grammar.
