/**
 * Lane-action catalogue for the detail view's primary-action + overflow
 * cluster (rendered in <app-detail-header>, top-right of the panel).
 *
 * The footer bar that previously hosted these buttons (the bottom-of-detail
 * "Human Review v" trigger + popover) was removed; the catalogue itself is
 * the same — index 0 is the lane's primary action and binds to Enter, the
 * remaining entries surface in the ⋯ overflow menu. Keeping this as a plain
 * TS module (no component) means the detail-header can render the cluster
 * directly without rehydrating a separate Angular component.
 */

export type TriageActionIntent =
  | { kind: 'move'; targetState: string }
  | { kind: 'moveToTop' }
  | { kind: 'delete' }
  | { kind: 'start' }
  | { kind: 'stop' }
  | { kind: 'editPrompt' }
  | { kind: 'showActivity' };

export interface TriageActionPayload {
  /** Stable id (e.g. 'mark-done') for testid + telemetry. */
  id: string;
  /** Display label, used by the parent for confirmation toasts. */
  label: string;
  intent: TriageActionIntent;
}

export type TriageButtonVariant = 'primary' | 'secondary' | 'danger';

export interface TriageButton {
  id: string;
  label: string;
  variant: TriageButtonVariant;
  intent: TriageActionIntent;
}

const PROMOTE_TO_PREP: TriageButton          = { id: 'promote-prep',       label: 'Promote to Preparation',           variant: 'primary',   intent: { kind: 'move', targetState: '1-preparation' } };
const PROMOTE_TO_READY: TriageButton         = { id: 'promote-ready',      label: 'Promote to Ready',                 variant: 'primary',   intent: { kind: 'move', targetState: '2-ready' } };
const PROMOTE_TO_READY_SKIP_PREP: TriageButton = { id: 'promote-ready-skip', label: 'Promote to Ready (skip prep)',  variant: 'secondary', intent: { kind: 'move', targetState: '2-ready' } };
const FORCE_TO_READY: TriageButton           = { id: 'force-to-ready',     label: 'Force to Ready',                   variant: 'secondary', intent: { kind: 'move', targetState: '2-ready' } };
const SEND_TO_BACKLOG: TriageButton          = { id: 'send-to-backlog',    label: 'Send to Backlog',                  variant: 'secondary', intent: { kind: 'move', targetState: '0-backlog' } };
const SEND_TO_PREP: TriageButton             = { id: 'send-to-prep',       label: 'Send to Preparation',              variant: 'secondary', intent: { kind: 'move', targetState: '1-preparation' } };
// "Move toward Completed / Archive" mirror "Send to Backlog": plain move
// actions surfaced in the overflow menu from (almost) every lane so the
// operator can park a card in 6-completed / 7-archive without opening the
// lane dropdown. overflowActionsFor() handles dedup + current-lane skip.
export const MOVE_TO_COMPLETED: TriageButton = { id: 'move-to-completed',   label: 'Move to Completed',                variant: 'secondary', intent: { kind: 'move', targetState: '6-completed' } };
export const MOVE_TO_ARCHIVE: TriageButton   = { id: 'move-to-archive',     label: 'Move to Archive',                  variant: 'secondary', intent: { kind: 'move', targetState: '7-archive' } };
export const EDIT_BUTTON: TriageButton       = { id: 'edit-prompt',        label: 'Edit prompt',                      variant: 'secondary', intent: { kind: 'editPrompt' } };
export const DELETE_BUTTON: TriageButton     = { id: 'delete',             label: 'Delete task',                      variant: 'danger',    intent: { kind: 'delete' } };

/**
 * Lane-specific button rows. Order matters: index 0 is the primary action
 * bound to Enter. Lanes that the user normally only observes
 * (`1a-orchestrator-prep`, `4-auto-review`) intentionally have no `primary`
 * entry — Enter is a no-op there to discourage rubber-stamp clicks.
 */
export const LANE_ACTIONS: Record<string, TriageButton[]> = {
  '0-backlog': [
    PROMOTE_TO_PREP,
    PROMOTE_TO_READY_SKIP_PREP,
    EDIT_BUTTON,
    DELETE_BUTTON,
  ],
  '1-preparation': [
    PROMOTE_TO_READY,
    SEND_TO_BACKLOG,
    EDIT_BUTTON,
  ],
  '1a-orchestrator-prep': [
    SEND_TO_BACKLOG,
    FORCE_TO_READY,
  ],
  '2-ready': [
    { id: 'run-now',     label: 'Run now',      variant: 'primary',   intent: { kind: 'start' } },
    { id: 'move-to-top', label: 'Move to top',  variant: 'secondary', intent: { kind: 'moveToTop' } },
    SEND_TO_BACKLOG,
    EDIT_BUTTON,
  ],
  '3-progress': [
    { id: 'stop-run',         label: 'Stop run',         variant: 'primary',   intent: { kind: 'stop' } },
    { id: 'view-live-output', label: 'View live output', variant: 'secondary', intent: { kind: 'showActivity' } },
  ],
  // 3b-code-not-complete: a task the runner parked after it exhausted its
  // auto-pickup retry budget without reaching review. The operator's natural
  // moves are to requeue it (after fixing context) or send it back for
  // preparation/triage.
  '3b-code-not-complete': [
    { id: 'send-back-to-ready', label: 'Send back to Ready (re-do)', variant: 'primary',   intent: { kind: 'move', targetState: '2-ready' } },
    SEND_TO_PREP,
    SEND_TO_BACKLOG,
    EDIT_BUTTON,
  ],
  '4-auto-review': [
    { id: 'force-accept', label: 'Force-accept (→ Review)', variant: 'secondary', intent: { kind: 'move', targetState: '5-human-review' } },
    { id: 'reissue',      label: 'Reissue (→ Progress)',          variant: 'secondary', intent: { kind: 'move', targetState: '3-progress' } },
  ],
  '5-human-review': [
    { id: 'mark-done',          label: 'Send to Complete',                          variant: 'primary',   intent: { kind: 'move', targetState: '6-completed' } },
    { id: 'send-back-to-ready', label: 'Send back to Ready (re-do)',                variant: 'secondary', intent: { kind: 'move', targetState: '2-ready' } },
    SEND_TO_BACKLOG,
  ],
  '6-completed': [
    // "Archive & Next" mirrors the review lanes' Complete-and-advance primary:
    // the move to 7-archive auto-advances the detail view to the next card in
    // 6-completed (TriageController.move → advanceToNextInLane). The "& Next"
    // wording makes the queue-sweep affordance explicit on the Completed lane.
    { id: 'archive', label: 'Archive & Next',      variant: 'primary',   intent: { kind: 'move', targetState: '7-archive' } },
    { id: 'reopen',  label: 'Re-open (→ Backlog)', variant: 'secondary', intent: { kind: 'move', targetState: '0-backlog' } },
  ],
  '7-archive': [
    { id: 'restore', label: 'Restore (→ Backlog)', variant: 'primary', intent: { kind: 'move', targetState: '0-backlog' } },
  ],
};

export const LANE_LABELS: Record<string, string> = {
  '0-backlog':              'Backlog',
  '1-preparation':          'Preparation',
  '1a-orchestrator-prep':   'Orchestrator Prep',
  '2-ready':                'Ready',
  '3-progress':             'In Progress',
  '3b-code-not-complete':   'Code not complete',
  '4-auto-review':          'Post Processing',
  '5-human-review':         'Review',
  '6-completed':            'Completed',
  '7-archive':              'Archive',
};

/**
 * Lanes the orchestrator owns: a job lands here because the runner picked
 * it up (3-progress) or the auto-reviewer is judging it (4-auto-review),
 * never because an operator chose to park it there. They are therefore not
 * valid manual move targets — `overflowActionsFor` strips any move action
 * pointing at them so neither the context menu nor the (model-fed) studio
 * overflow offers them. The lane dropdowns drop them from their nav options
 * separately (they are navigation-only and these lanes are not pageable
 * targets either).
 */
export const ORCHESTRATOR_CONTROLLED_LANES: ReadonlySet<string> = new Set([
  '3-progress',
  '4-auto-review',
]);

export function laneActionsFor(state: string): TriageButton[] {
  return LANE_ACTIONS[state] ?? [];
}

export function laneLabelFor(state: string): string {
  return LANE_LABELS[state] ?? state;
}

/** Returns the lane's primary (index 0 with variant=primary) or null. */
export function primaryActionFor(state: string): TriageButton | null {
  const list = laneActionsFor(state);
  return list.length > 0 && list[0].variant === 'primary' ? list[0] : null;
}

/**
 * Returns the overflow actions for a lane — everything that is not the
 * primary action, plus Edit and Delete as guaranteed safety nets if the
 * lane catalogue did not already list them.
 */
export function overflowActionsFor(state: string): TriageButton[] {
  const list = laneActionsFor(state);
  const rest = (primaryActionFor(state) ? list.slice(1) : list.slice()).filter(
    b => b.intent.kind !== 'move' || !ORCHESTRATOR_CONTROLLED_LANES.has(b.intent.targetState),
  );

  // Edit / Delete form the trailing "safety net" cluster. Hold any that the
  // lane already lists aside so the Move entries land before them — otherwise
  // a lane like 2-ready (which lists Edit in its catalogue) would render
  // Edit *between* Send to Backlog and the Move entries.
  const isTail = (b: TriageButton) => b.id === EDIT_BUTTON.id || b.id === DELETE_BUTTON.id;
  const body = rest.filter(b => !isTail(b));
  const tail = rest.filter(isTail);

  // "Move to Completed" / "Move to Archive" are guaranteed move actions,
  // added before the Edit/Delete cluster. Skip the target that equals the
  // current lane (a no-op) and any target a lane-specific action already
  // covers, so e.g. 5-human-review's "Send to Complete" primary or
  // 6-completed's "Archive" primary is not duplicated.
  const laneTargets = new Set(
    list
      .map(b => (b.intent.kind === 'move' ? b.intent.targetState : null))
      .filter((s): s is string => s !== null),
  );
  if (state !== '6-completed' && !laneTargets.has('6-completed')) body.push(MOVE_TO_COMPLETED);
  if (state !== '7-archive' && !laneTargets.has('7-archive')) body.push(MOVE_TO_ARCHIVE);

  const items = [...body, ...tail];
  if (!items.some(i => i.id === EDIT_BUTTON.id)) items.push(EDIT_BUTTON);
  if (!items.some(i => i.id === DELETE_BUTTON.id)) items.push(DELETE_BUTTON);
  return items;
}
