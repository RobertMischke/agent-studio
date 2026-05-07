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
  template: `
    <section class="triage" data-testid="triage-panel" aria-label="Triage actions">
      <div class="triage__counter" data-testid="triage-counter">
        @if (laneSize() > 0) {
          Job {{ laneIndex() + 1 }} of {{ laneSize() }} in {{ laneLabel() }}
        } @else {
          {{ laneLabel() }}
        }
        <span class="triage__hint" aria-hidden="true">
          j / k navigate · Enter primary · Esc close
        </span>
      </div>

      @if (mutationsBlocked()) {
        <div class="triage__blocked" role="status">
          Update in progress — actions paused.
        </div>
      }

      @if (buttons().length === 0) {
        <div class="triage__empty">No triage actions for this lane.</div>
      } @else {
        <div class="triage__actions" role="group">
          @for (b of buttons(); track b.id) {
            <button type="button"
                    class="triage__btn"
                    [class.triage__btn--primary]="b.variant === 'primary'"
                    [class.triage__btn--secondary]="b.variant === 'secondary'"
                    [class.triage__btn--danger]="b.variant === 'danger'"
                    [class.triage__btn--confirming]="confirmingId() === b.id"
                    [attr.data-testid]="'triage-action-' + b.id"
                    [attr.data-action-id]="b.id"
                    [disabled]="mutationsBlocked() || actingId() !== null"
                    [title]="buttonTooltip(b)"
                    (click)="onClick(b)">
              @if (confirmingId() === b.id) {
                Confirm: {{ b.label }}?
              } @else if (actingId() === b.id) {
                ⏳ {{ b.label }}…
              } @else {
                {{ b.label }}
              }
            </button>
          }
        </div>
      }
    </section>
  `,
  styles: [`
    :host { display: block; }
    /* Compact bar: counter + buttons share one row when there is room, so
       the panel costs minimal vertical space (the chat-first compression
       spec expects the protocol pane to keep > 68% of the viewport). */
    .triage {
      display: flex;
      flex-wrap: wrap;
      align-items: center;
      gap: 6px 12px;
      padding: 5px 10px;
      margin-top: 6px;
      background: rgba(15, 23, 42, 0.55);
      border: 1px solid rgba(255, 255, 255, 0.08);
      border-radius: 6px;
    }
    .triage__counter {
      display: inline-flex;
      flex-wrap: wrap;
      align-items: center;
      gap: 8px;
      font-size: 0.74rem;
      color: #cdd6f4;
      font-weight: 600;
      letter-spacing: 0.02em;
    }
    .triage__hint {
      color: rgba(255, 255, 255, 0.45);
      font-weight: 500;
      font-size: 0.66rem;
      letter-spacing: 0.04em;
      text-transform: none;
    }
    .triage__blocked {
      font-size: 0.72rem;
      color: #f9e2af;
      background: rgba(249, 226, 175, 0.08);
      border: 1px solid rgba(249, 226, 175, 0.25);
      border-radius: 4px;
      padding: 3px 8px;
    }
    .triage__empty {
      font-size: 0.74rem;
      color: rgba(205, 214, 244, 0.6);
      font-style: italic;
    }
    .triage__actions {
      display: inline-flex;
      flex-wrap: wrap;
      gap: 6px;
    }
    .triage__btn {
      cursor: pointer;
      font: inherit;
      font-size: 0.76rem;
      font-weight: 600;
      padding: 4px 10px;
      border-radius: 999px;
      border: 1px solid transparent;
      background: rgba(255, 255, 255, 0.06);
      color: #cdd6f4;
      transition: background 0.12s ease, border-color 0.12s ease, color 0.12s ease, transform 0.05s ease;
      white-space: nowrap;
    }
    .triage__btn:hover:not(:disabled) {
      background: rgba(255, 255, 255, 0.12);
      border-color: rgba(255, 255, 255, 0.16);
    }
    .triage__btn:active:not(:disabled) { transform: translateY(1px); }
    .triage__btn:disabled {
      opacity: 0.5;
      cursor: not-allowed;
    }
    .triage__btn--primary {
      background: rgba(137, 180, 250, 0.18);
      color: #89b4fa;
      border-color: rgba(137, 180, 250, 0.45);
    }
    .triage__btn--primary:hover:not(:disabled) {
      background: rgba(137, 180, 250, 0.28);
      color: #b4c5ff;
    }
    .triage__btn--secondary {
      background: rgba(255, 255, 255, 0.05);
      color: #cdd6f4;
      border-color: rgba(255, 255, 255, 0.12);
    }
    .triage__btn--danger {
      background: rgba(243, 139, 168, 0.10);
      color: #f38ba8;
      border-color: rgba(243, 139, 168, 0.35);
    }
    .triage__btn--danger:hover:not(:disabled) {
      background: rgba(243, 139, 168, 0.20);
      color: #ffb1c4;
    }
    .triage__btn--confirming {
      background: rgba(243, 139, 168, 0.30);
      color: #ffd1dd;
      border-color: rgba(243, 139, 168, 0.7);
    }
  `]
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
