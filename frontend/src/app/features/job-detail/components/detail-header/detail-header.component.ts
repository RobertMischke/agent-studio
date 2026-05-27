import { ChangeDetectionStrategy, Component, DestroyRef, ElementRef, HostListener, ViewChild, computed, effect, inject, input, output, signal } from '@angular/core';
import { JobInfo } from '../../../../models/task.model';
import { ModalStackService } from '../../../../services/modal-stack.service';
import {
  formatDateTime as fmtDateTime,
  formatRelativeShort as fmtRelativeShort,
  stateLabel as fmtStateLabel
} from '../../../../services/format.util';
import { NowTickService } from '../../../../services/now-tick.service';
import { projectIdentity } from '../../../../services/project-identity.util';
import { ProjectHygieneBadgeComponent } from '../hygiene-strip/project-hygiene-badge/project-hygiene-badge.component';

import { TooltipDirective } from '../../../../components/tooltip';
/**
 * Top header of the job-detail view: back button, editable title,
 * state pill, and the "Complete & Next" review action. Title-edit
 * state is owned by the parent and passed via inputs/outputs.
 */
@Component({
  selector: 'app-detail-header',
  standalone: true,
  changeDetection: ChangeDetectionStrategy.OnPush,
  imports: [ProjectHygieneBadgeComponent, TooltipDirective],
  templateUrl: './detail-header.component.html',
  styleUrl: './detail-header.component.scss'
})
export class DetailHeaderComponent {
  readonly info = input.required<JobInfo>();
  readonly editingTitle = input(false);
  readonly titleDraft = input<string>('');
  readonly savingTitle = input(false);
  readonly isReview = input(false);
  readonly completingAndNext = input(false);
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

  readonly back = output<void>();
  readonly startTitleEdit = output<void>();
  readonly cancelTitleEdit = output<void>();
  readonly saveTitle = output<void>();
  readonly titleDraftChange = output<string>();
  readonly completeAndNext = output<void>();
  readonly deleteRequested = output<void>();
  readonly stateChange = output<string>();
  readonly moveToTop = output<void>();
  readonly pagerPrev = output<void>();
  readonly pagerNext = output<void>();

  /**
   * Plain-text tooltip explaining the snapshot iteration. Stays inside
   * the `title` attribute (default browser delay, no rich HTML) per the
   * project's tooltip rule.
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
    { state: '5-human-review',        label: 'Human Review' },
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

  readonly menuOpen = signal(false);

  toggleMenu(event: Event) {
    event.stopPropagation();
    this.menuOpen.update(v => !v);
  }

  closeMenu() {
    this.menuOpen.set(false);
  }

  onDeleteClick(event: Event) {
    event.stopPropagation();
    this.menuOpen.set(false);
    this.deleteRequested.emit();
  }

  @HostListener('document:click')
  onDocumentClick() {
    if (this.menuOpen()) this.menuOpen.set(false);
  }

  // Escape routes through ModalStack so the dropdown closes only when
  // nothing modal sits above it.
  private readonly modalStack = inject(ModalStackService);
  private readonly menuDestroyRef = inject(DestroyRef);
  private menuStackDispose: (() => void) | null = null;
  private readonly menuStackEffect = effect(() => {
    const open = this.menuOpen();
    if (open && !this.menuStackDispose) {
      this.menuStackDispose = this.modalStack.push('detail-header-menu', () => this.menuOpen.set(false));
    } else if (!open && this.menuStackDispose) {
      this.menuStackDispose();
      this.menuStackDispose = null;
    }
  });
  private readonly menuStackTeardown = this.menuDestroyRef.onDestroy(() => this.menuStackDispose?.());

  @ViewChild('stateSelect') private stateSelectEl?: ElementRef<HTMLSelectElement>;

  /**
   * Force the lane dropdown's DOM value to follow `info().jobKey` even when
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
    if (this.lastSyncedJobKey === info.jobKey) return;
    this.lastSyncedJobKey = info.jobKey;
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
}
