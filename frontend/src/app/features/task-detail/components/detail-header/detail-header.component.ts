import { ChangeDetectionStrategy, Component, ElementRef, ViewChild, computed, effect, inject, input, output, signal } from '@angular/core';
import { TaskInfo } from '../../../../models/task.model';
import {
  formatDateTime as fmtDateTime,
  formatRelativeShort as fmtRelativeShort,
  stateLabel as fmtStateLabel
} from '../../../../services/format.util';
import { NowTickService } from '../../../../services/now-tick.service';
import { projectIdentity } from '../../../../services/project-identity.util';
import { ProjectHygieneBadgeComponent } from '../hygiene-strip/project-hygiene-badge/project-hygiene-badge.component';

import { TooltipDirective } from '../../../../components/tooltip';
import { MenuComponent, MenuItem, MenuItemClickEvent } from '../../../../components/menu';
import { NotificationService } from '../../../../services/notification.service';
import { copyTextToClipboard } from '../../../../services/clipboard.util';
import {
  TriageActionPayload,
  TriageButton,
  laneLabelFor,
  overflowActionsFor,
  primaryActionFor,
} from '../../state/triage-actions.model';
/**
 * Top header of the job-detail view: back button, editable title,
 * state pill, and — top-right — the lane's primary triage action plus
 * an overflow menu of the remaining lane actions. The bottom-of-detail
 * triage bar that used to host these is gone (the operator reported the
 * "Human Review v" trigger row still rendering after the first attempt
 * at folding it up). Title-edit state is owned by the parent and passed
 * via inputs/outputs.
 */
@Component({
  selector: 'app-detail-header',
  standalone: true,
  changeDetection: ChangeDetectionStrategy.OnPush,
  imports: [ProjectHygieneBadgeComponent, TooltipDirective, MenuComponent],
  templateUrl: './detail-header.component.html',
  styleUrl: './detail-header.component.scss'
})
export class DetailHeaderComponent {
  readonly info = input.required<TaskInfo>();
  readonly editingTitle = input(false);
  readonly titleDraft = input<string>('');
  readonly savingTitle = input(false);
  readonly changingState = input(false);
  readonly movingToTop = input(false);
  /** Lane pager position (1-based). 0 means no pager snapshot active. */
  readonly pagerPosition = input(0);
  /** Lane pager total. 0 hides the pager. */
  readonly pagerTotal = input(0);
  readonly pagerCanPrev = input(false);
  readonly pagerCanNext = input(false);
  /** Human-readable label of the snapshot's original lane (e.g. "Ready"). */
  readonly pagerLaneLabel = input<string>('');
  /** Index of the open job inside the live lane peers (0-based; -1 == unknown). */
  readonly laneIndex = input(0);
  /** Total peers in the current lane (0 == no peers). */
  readonly laneSize = input(0);
  /** True while the update-service is mid-update; disables triage actions. */
  readonly mutationsBlocked = input(false);
  /** Stable id of the triage action currently in flight (null when idle). */
  readonly triageActingId = input<string | null>(null);

  readonly back = output<void>();
  readonly startTitleEdit = output<void>();
  readonly cancelTitleEdit = output<void>();
  readonly saveTitle = output<void>();
  readonly titleDraftChange = output<string>();
  /**
   * Delete request. The overflow menu's Delete row routes here instead of
   * through `triageAction` so the existing `boardMutations.deleteFromDetail`
   * confirm dialog + pager-aware advance stay in charge of destructive ops.
   */
  readonly deleteRequested = output<void>();
  readonly stateChange = output<string>();
  readonly moveToTop = output<void>();
  readonly pagerPrev = output<void>();
  readonly pagerNext = output<void>();
  /** Lane-action chosen via the primary button or the overflow menu. */
  readonly triageAction = output<TriageActionPayload>();

  /**
   * Tooltip text explaining the snapshot iteration, surfaced through the
   * app's canonical `[appTooltip]` directive (single visual standard,
   * instant hover). Plain readable language, no embedded markup.
   */
  readonly pagerTooltip = computed(() => {
    const total = this.pagerTotal();
    if (total <= 0) return '';
    const lane = this.pagerLaneLabel() || 'this lane';
    const pos = this.pagerPosition();
    if (pos <= 0) {
      return `This task has left the ${lane} lane. ${total} job${total === 1 ? '' : 's'} remain in the captured iteration.`;
    }
    return `Iterating jobs in the ${lane} lane. Showing job ${pos} of ${total} captured when you entered this view.`;
  });

  /**
   * Lane dropdown options surfaced in the detail header. The order mirrors
   * the kanban left-to-right flow so picking a target reads like "advance"
   * vs "send back". `1a-orchestrator-prep` and `1b-needs-human-review` are
   * orchestrator-managed lanes (ADR-0026) — listed but unusual to pick by
   * hand, hence the discreet labels.
   */
  readonly laneOptions: readonly { state: string; label: string }[] = [
    { state: '1-preparation',         label: 'Preparation' },
    { state: '1a-orchestrator-prep',  label: 'Orch Prep' },
    { state: '1b-needs-human-review', label: 'Needs Clarification' },
    { state: '2-ready',               label: 'Ready' },
    { state: '3-progress',            label: 'In Progress' },
    { state: '4-auto-review',         label: 'Auto Review' },
    { state: '5-human-review',        label: 'Review' },
    { state: '6-completed',           label: 'Completed' },
    { state: '7-archive',             label: 'Archive' },
  ];

  isStandardLane(state: string): boolean {
    return this.laneOptions.some(o => o.state === state);
  }

  /** "Do Next" only makes sense while the task is queued in 2-ready and not yet
   *  picked up. The state-select dropdown is the path to bring it into ready
   *  from a different lane first; after that the button surfaces. */
  readonly canMoveToTop = computed(() => this.info().state === '2-ready');

  onStateSelect(event: Event) {
    const target = event.target as HTMLSelectElement;
    const next = target.value;
    if (!next || next === this.info().state) return;
    this.stateChange.emit(next);
  }

  // --- triage cluster (primary + overflow) --------------------------------

  /** Lane label for tooltips / aria-text on the overflow trigger. */
  readonly triageLaneLabel = computed(() => laneLabelFor(this.info().state));

  /** Index 0 of the lane's action list when it carries a primary variant. */
  readonly triagePrimary = computed<TriageButton | null>(() =>
    primaryActionFor(this.info().state),
  );

  /** Remaining lane actions + always-on Edit/Delete fallbacks. */
  readonly triageOverflow = computed<TriageButton[]>(() =>
    overflowActionsFor(this.info().state),
  );

  /** True when a primary or any overflow action is available. */
  readonly hasTriageActions = computed(
    () => this.triagePrimary() !== null || this.triageOverflow().length > 0,
  );

  /**
   * Counter shown on the overflow button (and read by E2E specs to verify
   * the panel is anchored to the right lane). Mirrors the wording of the
   * old footer-bar counter so legacy spec assertions still pass.
   */
  readonly triageCounterText = computed(() => {
    const total = this.laneSize();
    const lane = this.triageLaneLabel();
    if (total <= 0) return `in ${lane}`;
    const pos = Math.max(this.laneIndex() + 1, 1);
    return `Task ${pos} of ${total} in ${lane}`;
  });

  readonly triageOverflowOpen = signal(false);
  readonly triageOverflowAnchor = signal<HTMLElement | null>(null);

  readonly triageMenuItems = computed<MenuItem[]>(() => {
    const disabled = this.mutationsBlocked() || this.triageActingId() !== null;
    return this.triageOverflow().map<MenuItem>(b => ({
      kind: 'row',
      id: b.id,
      label: b.label,
      danger: b.variant === 'danger',
      disabled,
    }));
  });

  primaryTooltip(): string {
    const p = this.triagePrimary();
    if (!p) return '';
    if (this.mutationsBlocked()) return 'Update in progress — actions paused.';
    if (this.triageActingId() === p.id) return `${p.label}…`;
    return `${p.label} (Enter)`;
  }

  overflowTooltip(): string {
    if (this.mutationsBlocked()) return 'Update in progress — actions paused.';
    const count = this.triageOverflow().length;
    return `${this.triageLaneLabel()} actions (${count})`;
  }

  onPrimaryClick(): void {
    const p = this.triagePrimary();
    if (!p) return;
    this.emitTriage(p);
  }

  toggleTriageOverflow(event: MouseEvent): void {
    event.stopPropagation();
    if (this.mutationsBlocked()) return;
    this.triageOverflowAnchor.set(event.currentTarget as HTMLElement);
    this.triageOverflowOpen.update(v => !v);
  }

  closeTriageOverflow(): void {
    this.triageOverflowOpen.set(false);
  }

  onTriageMenuItemClick(ev: MenuItemClickEvent): void {
    const button = this.triageOverflow().find(b => b.id === ev.id);
    if (!button) return;
    // Delete keeps its dedicated output (boardMutations.deleteFromDetail
    // owns the confirm dialog + pager-aware advance). The rest of the lane
    // actions flow through the triage controller via `triageAction`.
    if (button.id === 'delete') {
      this.triageOverflowOpen.set(false);
      this.deleteRequested.emit();
      return;
    }
    this.emitTriage(button);
  }

  /** Called by the parent on Enter when no input is focused. */
  triggerPrimary(): void {
    this.onPrimaryClick();
  }

  private emitTriage(button: TriageButton): void {
    if (this.mutationsBlocked() || this.triageActingId() !== null) return;
    this.triageOverflowOpen.set(false);
    this.triageAction.emit({ id: button.id, label: button.label, intent: button.intent });
  }

  @ViewChild('stateSelect') private stateSelectEl?: ElementRef<HTMLSelectElement>;

  /**
   * Force the lane dropdown's DOM value to follow `info().taskKey` even when
   * the bound state string did not change. Without this, an auto-advance
   * from one job to another inside the SAME lane (e.g. triaging 2-ready)
   * leaves the user's last `selectOption` choice on screen because
   * Angular's [value] binding skips the DOM write when its previous value
   * was identical. The effect runs only on job switch; same-job state
   * updates already flow through the existing [value] binding.
   */
  private lastSyncedJobKey: string | null = null;
  private syncStateSelectOnJobSwitch = effect(() => {
    const info = this.info();
    if (this.lastSyncedJobKey === info.taskKey) return;
    this.lastSyncedJobKey = info.taskKey;
    const el = this.stateSelectEl?.nativeElement;
    if (el && el.value !== info.state) {
      queueMicrotask(() => { if (el) el.value = info.state; });
    }
  });

  @ViewChild('titleInput') private titleInputEl?: ElementRef<HTMLInputElement>;

  /** Auto-focus the input when editing turns on (parity with prior behavior). */
  private focusOnEdit = effect(() => {
    if (this.editingTitle()) {
      queueMicrotask(() => this.titleInputEl?.nativeElement.select());
    }
  });

  private readonly nowTick = inject(NowTickService).now;

  readonly relativeCreated = computed(() => fmtRelativeShort(this.info().createdAt, this.nowTick()));
  readonly createdAtTooltip = computed(() => fmtDateTime(this.info().createdAt));

  readonly identity = computed(() => projectIdentity(this.info().projectName));

  stateLabel(state: string): string { return fmtStateLabel(state); }

  // Title right-click context menu for copy actions
  private readonly notifs = inject(NotificationService);
  readonly titleContextMenu = signal<{ x: number; y: number } | null>(null);
  readonly titleCtxMenuItems = computed<readonly MenuItem[]>(() => {
    const info = this.info();
    const items: MenuItem[] = [
      { kind: 'row', id: 'copy-name', label: 'Copy Name' },
      { kind: 'row', id: 'copy-id', label: 'Copy ID' },
    ];
    if (info.key) {
      items.push({ kind: 'row', id: 'copy-key', label: `Copy Key (${info.key})` });
    }
    return items;
  });

  openTitleContextMenu(event: MouseEvent): void {
    event.preventDefault();
    event.stopPropagation();
    this.titleContextMenu.set({ x: event.clientX, y: event.clientY });
  }

  closeTitleContextMenu(): void {
    this.titleContextMenu.set(null);
  }

  onTitleCtxMenuItemClick(ev: MenuItemClickEvent): void {
    const info = this.info();
    let text = '';
    let label = '';
    if (ev.id === 'copy-name') { text = info.title || info.id; label = 'Name'; }
    else if (ev.id === 'copy-id') { text = info.id; label = 'ID'; }
    else if (ev.id === 'copy-key' && info.key) { text = info.key; label = 'Key'; }
    if (text) {
      copyTextToClipboard(text).then(ok => {
        if (ok) this.notifs.success(`${label} copied`);
      });
    }
  }
}
