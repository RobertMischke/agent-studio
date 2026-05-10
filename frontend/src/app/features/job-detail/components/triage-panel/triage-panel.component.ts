import {
  ChangeDetectionStrategy,
  Component,
  computed,
  HostListener,
  input,
  output,
  signal
} from '@angular/core';

/**
 * Triage panel: lane-specific decision row at the bottom of the detail view.
 *
 * Goal (see prompts/runtime triage spec): a user walking a single lane should
 * be able to read → decide → next without leaving the detail panel. Keyboard
 * `j`/`k` (or arrows) navigate within the lane, `Enter` triggers the primary
 * action, `Esc` closes. Destructive actions (Delete, Archive) require a
 * confirm-prompt on first click; the second click commits.
 *
 * The panel is presentational: the parent owns the API mutation. Each button
 * emits a typed `TriageActionPayload` that the parent translates into an
 * existing job endpoint (move / move-to-top / delete / start / stop) — no new
 * REST surface. While `mutationsBlocked` is true (an update-service update is
 * in flight) every action is disabled with a tooltip.
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

interface TriageButton {
  id: string;
  label: string;
  variant: 'primary' | 'secondary' | 'danger';
  intent: TriageActionIntent;
  /** Hidden hint shown on first click when destructive. */
  confirmRequired?: boolean;
}

const PROMOTE_TO_PREP: TriageButton = { id: 'promote-prep',     label: 'Promote to Preparation', variant: 'primary',   intent: { kind: 'move', targetState: '1-preparation' } };
const PROMOTE_TO_READY: TriageButton = { id: 'promote-ready',   label: 'Promote to Ready',       variant: 'primary',   intent: { kind: 'move', targetState: '2-ready' } };
const PROMOTE_TO_READY_SKIP_PREP: TriageButton = { id: 'promote-ready-skip', label: 'Promote to Ready (skip prep)', variant: 'secondary', intent: { kind: 'move', targetState: '2-ready' } };
const FORCE_TO_READY: TriageButton = { id: 'force-to-ready',    label: 'Force to Ready',         variant: 'secondary', intent: { kind: 'move', targetState: '2-ready' } };
const SEND_TO_BACKLOG: TriageButton = { id: 'send-to-backlog',  label: 'Send to Backlog',        variant: 'secondary', intent: { kind: 'move', targetState: '0-backlog' } };
const SEND_TO_PREP: TriageButton = { id: 'send-to-prep',        label: 'Send to Preparation',    variant: 'secondary', intent: { kind: 'move', targetState: '1-preparation' } };
const EDIT_BTN: TriageButton = { id: 'edit-prompt',             label: 'Edit',                   variant: 'secondary', intent: { kind: 'editPrompt' } };
const DELETE_BTN: TriageButton = { id: 'delete',                label: 'Delete',                 variant: 'danger',    intent: { kind: 'delete' }, confirmRequired: true };

/**
 * Lane-specific button rows. Order matters: index 0 is the primary action
 * bound to Enter. Lanes that the user normally only observes
 * (`1a-orchestrator-prep`, `4-auto-review`) intentionally have no `primary`
 * pill — Enter is a no-op there to discourage rubber-stamp clicks.
 */
const LANE_ACTIONS: Record<string, TriageButton[]> = {
  '0-backlog': [
    PROMOTE_TO_PREP,
    PROMOTE_TO_READY_SKIP_PREP,
    EDIT_BTN,
    DELETE_BTN
  ],
  '1-preparation': [
    PROMOTE_TO_READY,
    SEND_TO_BACKLOG,
    EDIT_BTN
  ],
  '1a-orchestrator-prep': [
    SEND_TO_BACKLOG,
    FORCE_TO_READY
  ],
  '1b-needs-human-review': [
    SEND_TO_PREP,
    SEND_TO_BACKLOG,
    FORCE_TO_READY
  ],
  '2-ready': [
    { id: 'run-now',     label: 'Run now',      variant: 'primary',   intent: { kind: 'start' } },
    { id: 'move-to-top', label: 'Move to top',  variant: 'secondary', intent: { kind: 'moveToTop' } },
    SEND_TO_BACKLOG,
    EDIT_BTN
  ],
  '3-progress': [
    { id: 'stop-run',         label: 'Stop run',         variant: 'primary',   intent: { kind: 'stop' } },
    { id: 'view-live-output', label: 'View live output', variant: 'secondary', intent: { kind: 'showActivity' } }
  ],
  '3a-failed-pickup': [
    { id: 'retry-from-ready', label: 'Retry from Ready', variant: 'primary',   intent: { kind: 'move', targetState: '2-ready' } },
    SEND_TO_BACKLOG,
    { id: 'archive',          label: 'Archive',          variant: 'danger',    intent: { kind: 'move', targetState: '7-archive' }, confirmRequired: true }
  ],
  '4-auto-review': [
    { id: 'force-accept', label: 'Force-accept (→ Human Review)', variant: 'secondary', intent: { kind: 'move', targetState: '5-human-review' } },
    { id: 'reissue',      label: 'Reissue (→ Progress)',          variant: 'secondary', intent: { kind: 'move', targetState: '3-progress' } }
  ],
  '5-human-review': [
    { id: 'mark-done',         label: 'Mark as Done (→ Completed)',          variant: 'primary',   intent: { kind: 'move', targetState: '6-completed' } },
    { id: 'send-back-to-ready',label: 'Send back to Ready (re-do)',                variant: 'secondary', intent: { kind: 'move', targetState: '2-ready' } },
    SEND_TO_BACKLOG,
    { id: 'need-clarification',label: 'Need clarification (→ Needs Human Review)', variant: 'secondary', intent: { kind: 'move', targetState: '1b-needs-human-review' } }
  ],
  '6-completed': [
    { id: 'archive', label: 'Archive',                 variant: 'primary',   intent: { kind: 'move', targetState: '7-archive' } },
    { id: 'reopen',  label: 'Re-open (→ Backlog)', variant: 'secondary', intent: { kind: 'move', targetState: '0-backlog' } }
  ],
  '7-archive': [
    { id: 'restore', label: 'Restore (→ Backlog)', variant: 'primary', intent: { kind: 'move', targetState: '0-backlog' } }
  ]
};

const LANE_LABELS: Record<string, string> = {
  '0-backlog':              'Backlog',
  '1-preparation':          'Preparation',
  '1a-orchestrator-prep':   'Orchestrator Prep',
  '1b-needs-human-review':  'Needs Clarification',
  '2-ready':                'Ready',
  '3-progress':             'In Progress',
  '3a-failed-pickup':       'Failed Pickup',
  '4-auto-review':          'Auto Review',
  '5-human-review':         'Human Review',
  '6-completed':            'Completed',
  '7-archive':              'Archive'
};

@Component({
  selector: 'app-triage-panel',
  standalone: true,
  changeDetection: ChangeDetectionStrategy.OnPush,
  templateUrl: './triage-panel.component.html',
  styleUrl: './triage-panel.component.scss'
})
export class TriagePanelComponent {
  readonly laneState = input.required<string>();
  readonly laneIndex = input(0);
  readonly laneSize = input(0);
  readonly mutationsBlocked = input(false);
  /** Set by the parent while an action's API call is in flight. */
  readonly actingId = input<string | null>(null);

  readonly action = output<TriageActionPayload>();

  /** Tracks the destructive button awaiting confirm. Cleared on a 4 s timeout. */
  readonly confirmingId = signal<string | null>(null);
  private confirmTimer: ReturnType<typeof setTimeout> | null = null;

  readonly buttons = computed<TriageButton[]>(() => LANE_ACTIONS[this.laneState()] ?? []);

  readonly laneLabel = computed(() => LANE_LABELS[this.laneState()] ?? this.laneState());

  readonly primaryButton = computed<TriageButton | null>(() => {
    const list = this.buttons();
    return list.length > 0 && list[0].variant === 'primary' ? list[0] : null;
  });

  /** Called by the parent when Enter is pressed and an input isn't focused. */
  triggerPrimary(): void {
    const p = this.primaryButton();
    if (p) this.onClick(p);
  }

  buttonTooltip(b: TriageButton): string {
    if (this.mutationsBlocked()) return 'Update in progress';
    if (b.confirmRequired && this.confirmingId() !== b.id) return `${b.label} — click again to confirm`;
    return b.label;
  }

  onClick(b: TriageButton): void {
    if (this.mutationsBlocked() || this.actingId() !== null) return;
    if (b.confirmRequired && this.confirmingId() !== b.id) {
      this.confirmingId.set(b.id);
      this.scheduleConfirmTimeout();
      return;
    }
    this.clearConfirm();
    this.action.emit({ id: b.id, label: b.label, intent: b.intent });
  }

  private scheduleConfirmTimeout(): void {
    if (this.confirmTimer != null) clearTimeout(this.confirmTimer);
    this.confirmTimer = setTimeout(() => this.confirmingId.set(null), 4000);
  }

  private clearConfirm(): void {
    this.confirmingId.set(null);
    if (this.confirmTimer != null) {
      clearTimeout(this.confirmTimer);
      this.confirmTimer = null;
    }
  }

  // Reset the confirm chip if the lane changes underneath us (auto-advance).
  @HostListener('document:keydown.escape')
  onEscape(): void { this.clearConfirm(); }
}
