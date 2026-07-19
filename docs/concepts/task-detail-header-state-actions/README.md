# Mockups: Task Detail Header State Actions

Concept proposal for the task-detail header action cluster. This consolidates
ASS-565, ASS-564, and the feedback about "Force-accept" / "Edit prompt" into
one UX direction before implementation.

> **Shipped labels (post-implementation note).** The label tables below are the
> original concept. Two human-facing labels later diverged from this proposal
> and are now the source of truth in the UI:
> - The `5-human-review` primary (`mark-done`) ships as **"Merge into Develop"**
>   (not the proposed `→ Completed`); it still moves to `6-completed`.
> - The `6-completed` lane is displayed as **"Delivered"**, and the overflow
>   move action `move-to-completed` reads **"Move to Delivered"**.
>
> The action ids, intents, and state keys are unchanged. See
> `frontend/src/app/features/task-detail/state/triage-actions.model.ts` and the
> lane concept docs (`docs/in-app-help/lane-guides/lane-5-human-review.md`,
> `lane-6-completed.md`) for the current wording.

## Current Catalogue Analysis

Source of truth today is
`frontend/src/app/features/task-detail/state/triage-actions.model.ts`.
`LANE_ACTIONS` is keyed by source lane; index 0 is primary when it has
`variant: 'primary'`, and the remaining actions render in the overflow menu.
`overflowActionsFor` then appends extra safety actions.

The current model has five problems:

1. Move labels require interpretation. Examples: `Force-accept (→ Review)`,
   `Force to Ready`, `Promote to Ready (skip prep)`, `Re-open (→ Backlog)`.
   The verbs sound different even though they all move the task to another
   lane.
2. "Force-accept" reads like accepting or completing work, but it only moves
   an Auto Review task to Human Review.
3. `Edit prompt` is forced into overflow even though the action is broken in
   this header context. Prompt editing already has a real surface in the prompt
   pane.
4. Completion has competing wording across the surface: `Complete`,
   `Mark as Done`, and `Send to Complete`. The operator needs one completion
   action.
5. Undo must be designed together with move actions. The existing
   `UndoController` already covers lane reverts and order restoration, but the
   header catalogue still reads like a set of irreversible commands.

Two current implementation details matter for the proposal:

1. `overflowActionsFor` filters move targets in orchestrator-controlled lanes
   (`3-progress`, `4-auto-review`). That means an Auto Review `reissue` move to
   `3-progress` needs an explicit semantic exception if it should remain
   reachable.
2. The current task-detail `LANE_LABELS` map says `5-human-review: 'Review'`,
   while this concept uses the more explicit destination label `Human Review`.
   The implementation should either update that map for task-detail actions or
   add an action-specific destination-label map; do not accidentally ship
   `→ Review` when the goal is `→ Human Review`.

## Proposal

Use the destination lane as the label for every plain lane move:

```text
→ <Lane label>
```

The arrow is part of the text label, not an icon. That keeps context-menu rows
text-only and avoids a separate decorative leading icon. The lighter label is
preferable to `Move: <Lane>` because it is faster to scan, matches the existing
operator shorthand, and does not introduce another verb. If a future design
system forbids the arrow character even as text, the fallback is `Move:
<Lane label>`; do not mix the two forms in one menu.

Only plain moves use this form. Real commands keep verbs:

| Command kind | Label style |
|---|---|
| Start a queued run | `Run now` |
| Stop a live run | `Stop run` |
| Open output | `View live output` |
| Reissue from Auto Review | `Reissue` |
| Same-lane reorder | `Move to top` |
| Destructive delete | `Delete task` |

`Reissue` stays as the one semantic exception. It is not just a neutral lane
move: it asks the system to run the task again. The menu label should be
`Reissue`; tooltip text can say `Run this task again.` Tests should lock that
`reissue` remains visible from Auto Review even though its target state is
`3-progress`.

## Primary Action Rule

Primary means the one most likely next step for the current lane, bound to
Enter. Observation and judgment lanes keep no primary so Enter cannot rubber
stamp a review decision.

| Lane | Primary |
|---|---|
| Backlog | `→ Preparation` |
| Preparation | `→ Ready` |
| Orchestrator Prep | none |
| Ready | `Run now` |
| In Progress | `Stop run` |
| Code not complete | `→ Ready` |
| Auto Review | none |
| Human Review | `→ Completed` |
| Completed | `→ Archive` |
| Archive | `→ Backlog` |

## Overflow Menu Mockups

### Auto Review

No primary action. The operator must choose deliberately.

```text
Task Detail Header                                  [state: Auto Review] [ ... ]

Overflow menu
┌──────────────────────────┐
│ → Human Review          │
│ Reissue                  │
├──────────────────────────┤
│ Delete task              │
└──────────────────────────┘
```

Notes:

- `→ Human Review` replaces `Force-accept (→ Review)`.
- `Reissue` is visible despite targeting `3-progress`.
- No `Edit prompt`.
- Delete is separated at the bottom.

### Ready

Primary is a real command, not a move.

```text
Task Detail Header                     [state: Ready] [ Run now ] [ ... ]

Overflow menu
┌──────────────────────────┐
│ Move to top              │
│ → Backlog               │
│ → Completed             │
│ → Archive               │
├──────────────────────────┤
│ Delete task              │
└──────────────────────────┘
```

Notes:

- `Move to top` keeps its verb because it reorders inside the same lane.
- `Run now` remains the Enter-bound primary.

### Human Review

Completion is a single action.

```text
Task Detail Header              [state: Human Review] [ → Completed ] [ ... ]

Overflow menu
┌──────────────────────────┐
│ → Ready                 │
│ → Backlog               │
│ → Needs Clarification   │
│ → Archive               │
├──────────────────────────┤
│ Delete task              │
└──────────────────────────┘
```

Notes:

- `→ Completed` replaces `Mark as Done`, `Send to Complete`, and any redundant
  `Complete` button.
- `→ Needs Clarification` is included only if the current state machine exposes
  that lane as a legal target from Human Review. If not, implementation should
  omit it until the backend supports it.

### Completed

Archiving is the normal sweep action.

```text
Task Detail Header                 [state: Completed] [ → Archive ] [ ... ]

Overflow menu
┌──────────────────────────┐
│ → Backlog               │
├──────────────────────────┤
│ Delete task              │
└──────────────────────────┘
```

Notes:

- `→ Archive` replaces `Archive & Next` in the label. The existing
  auto-advance behavior can remain, but the label should describe the state
  move, not the pager side effect.

## Final Label Table

Plain move labels derive from `laneLabelFor(targetState)`. These rows pin the
human-facing label by current source lane and action id.

| Source lane | Action id | Intent | New label | Primary |
|---|---|---|---|---|
| `0-backlog` | `promote-prep` | move `1-preparation` | `→ Preparation` | yes |
| `0-backlog` | `promote-ready-skip` | move `2-ready` | `→ Ready` | no |
| `0-backlog` | `move-to-completed` | move `6-completed` | `→ Completed` | no |
| `0-backlog` | `move-to-archive` | move `7-archive` | `→ Archive` | no |
| `0-backlog` | `delete` | delete | `Delete task` | no |
| `1-preparation` | `promote-ready` | move `2-ready` | `→ Ready` | yes |
| `1-preparation` | `send-to-backlog` | move `0-backlog` | `→ Backlog` | no |
| `1-preparation` | `move-to-completed` | move `6-completed` | `→ Completed` | no |
| `1-preparation` | `move-to-archive` | move `7-archive` | `→ Archive` | no |
| `1-preparation` | `delete` | delete | `Delete task` | no |
| `1a-orchestrator-prep` | `send-to-backlog` | move `0-backlog` | `→ Backlog` | no |
| `1a-orchestrator-prep` | `force-to-ready` | move `2-ready` | `→ Ready` | no |
| `1a-orchestrator-prep` | `delete` | delete | `Delete task` | no |
| `2-ready` | `run-now` | start | `Run now` | yes |
| `2-ready` | `move-to-top` | moveToTop | `Move to top` | no |
| `2-ready` | `send-to-backlog` | move `0-backlog` | `→ Backlog` | no |
| `2-ready` | `move-to-completed` | move `6-completed` | `→ Completed` | no |
| `2-ready` | `move-to-archive` | move `7-archive` | `→ Archive` | no |
| `2-ready` | `delete` | delete | `Delete task` | no |
| `3-progress` | `stop-run` | stop | `Stop run` | yes |
| `3-progress` | `view-live-output` | showActivity | `View live output` | no |
| `3b-code-not-complete` | `send-back-to-ready` | move `2-ready` | `→ Ready` | yes |
| `3b-code-not-complete` | `send-to-prep` | move `1-preparation` | `→ Preparation` | no |
| `3b-code-not-complete` | `send-to-backlog` | move `0-backlog` | `→ Backlog` | no |
| `3b-code-not-complete` | `move-to-completed` | move `6-completed` | `→ Completed` | no |
| `3b-code-not-complete` | `move-to-archive` | move `7-archive` | `→ Archive` | no |
| `3b-code-not-complete` | `delete` | delete | `Delete task` | no |
| `4-auto-review` | `force-accept` | move `5-human-review` | `→ Human Review` | no |
| `4-auto-review` | `reissue` | move `3-progress` | `Reissue` | no |
| `4-auto-review` | `delete` | delete | `Delete task` | no |
| `5-human-review` | `mark-done` | move `6-completed` | `→ Completed` | yes |
| `5-human-review` | `send-back-to-ready` | move `2-ready` | `→ Ready` | no |
| `5-human-review` | `send-to-backlog` | move `0-backlog` | `→ Backlog` | no |
| `5-human-review` | `needs-clarification` | move supported needs-clarification lane | `→ Needs Clarification` | no |
| `5-human-review` | `move-to-archive` | move `7-archive` | `→ Archive` | no |
| `5-human-review` | `delete` | delete | `Delete task` | no |
| `6-completed` | `archive` | move `7-archive` | `→ Archive` | yes |
| `6-completed` | `reopen` | move `0-backlog` | `→ Backlog` | no |
| `6-completed` | `delete` | delete | `Delete task` | no |
| `7-archive` | `restore` | move `0-backlog` | `→ Backlog` | yes |
| `7-archive` | `move-to-completed` | move `6-completed` | `→ Completed` | no |
| `7-archive` | `delete` | delete | `Delete task` | no |

Implementation should delete the `editPrompt` header action from the triage
catalogue:

- Remove `EDIT_BUTTON`.
- Remove the `editPrompt` intent from `TriageActionIntent` if the compiler
  shows no remaining production caller.
- Remove the `case 'editPrompt'` branches in the detail and studio dispatchers
  if they become unreachable.
- Keep the actual Prompt pane editing affordance; only the broken header
  overflow shortcut disappears.

## Undo Design

Every header move should offer a non-blocking toast:

```text
bottom-right toast
┌──────────────────────────────────────────────────────────┐
│ Moved "fix-header-actions" → Completed        [ Undo ]  │
└──────────────────────────────────────────────────────────┘
```

Required behavior:

- Lane moves capture previous lane and previous order before mutation.
- Undo restores both lane and order.
- `Move to top` captures and restores the previous lane order.
- New undoable action supersedes the previous undo toast.
- Toast is non-blocking and should not cover the top-right overflow menu.
- Delete should become soft-delete-undoable in the same UX family. If the
  backend still hard-deletes, implementation should split delete undo into a
  backend slice rather than pretending the header can restore it.

The toast copy should use the destination lane label, not the old verb. Example:
`Moved "Task title" → Completed`.

Current state: lane-move and move-to-top undo already exist in
`UndoController`; delete undo does not, because delete currently removes the
folder. This concept keeps delete in the same UX family but requires a backend
soft-delete/restore slice before `Delete task` can truthfully be undoable.

## Complete De-Dup

There should be one completion action from Human Review:

```text
[ → Completed ]
```

Remove or rename any separate `Complete`, `Mark as Done`, or
`Send to Complete` surface that appears next to the same header cluster. The
single action moves to `6-completed`; pager auto-advance may remain an
implementation behavior but should not create a second visible completion
button.

## Superseded Work

This concept supersedes:

- ASS-565, redundant Complete button / Mark-as-Done wording.
- ASS-564, undo toast for state moves.
- The ad-hoc feedback to rename Force-accept and remove broken Edit prompt.

Those tasks should be annotated through the board as superseded by this concept
so the queue does not implement three conflicting action catalogues. If the API
does not expose a safe description-edit operation, leave that annotation to the
operator rather than editing task folders directly.

## Implementation Plan

1. Add a `moveLabel(targetState)` helper in `triage-actions.model.ts` that
   returns `→ ${laneLabelFor(targetState)}` for plain lane moves.
2. Rename action labels in `LANE_ACTIONS` according to the table above. Keep
   intents and target states unchanged except for adding a legal
   needs-clarification action only if the backend already supports it.
3. Remove `EDIT_BUTTON`, the `editPrompt` intent, and the forced append in
   `overflowActionsFor` if no production caller remains.
4. Preserve `Reissue` as a semantic exception and adjust the
   orchestrator-controlled-lane filter so this specific action remains visible
   from Auto Review.
5. Keep observe/review lanes without a primary where specified, especially
   `4-auto-review` and `1a-orchestrator-prep`.
6. Wire all move actions through the existing undo controller. Confirm the
   toast appears bottom-right and restores lane plus order.
7. Split delete undo if needed: add a soft-delete/restore backend endpoint, then
   route `Delete task` through the same toast pattern.
8. Update unit tests for `primaryActionFor`, `overflowActionsFor`, labels,
   absence of `edit-prompt`, and visible `reissue`.
9. Add or update a focused Playwright spec for the key lanes: Ready,
   Auto Review, Human Review, and Completed.
10. Verify visually in the in-app browser after implementation with the menu
    open in desktop and mobile widths.
