import { ChangeDetectionStrategy, Component, ElementRef, HostListener, ViewChild, computed, effect, inject, input, output, signal } from '@angular/core';
import { JobInfo } from '../../../../models/job.model';
import {
  formatDateTime as fmtDateTime,
  formatRelativeShort as fmtRelativeShort,
  stateLabel as fmtStateLabel
} from '../../../../services/format.util';
import { NowTickService } from '../../../../services/now-tick.service';
import { projectIdentity } from '../../../../services/project-identity.util';
import { ProjectHygieneBadgeComponent } from '../hygiene-strip/project-hygiene-badge.component';

/**
 * Top header of the job-detail view: back button, editable title,
 * state pill, and the "Complete & Next" review action. Title-edit
 * state is owned by the parent and passed via inputs/outputs.
 */
@Component({
  selector: 'app-detail-header',
  standalone: true,
  changeDetection: ChangeDetectionStrategy.OnPush,
  imports: [ProjectHygieneBadgeComponent],
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

  readonly back = output<void>();
  readonly startTitleEdit = output<void>();
  readonly cancelTitleEdit = output<void>();
  readonly saveTitle = output<void>();
  readonly titleDraftChange = output<string>();
  readonly completeAndNext = output<void>();
  readonly deleteRequested = output<void>();
  readonly stateChange = output<string>();
  readonly moveToTop = output<void>();

  /**
   * Lane dropdown options surfaced in the detail header. The order mirrors
   * the kanban left-to-right flow so picking a target reads like "advance"
   * vs "send back". `1a-orchestrator-prep` and `1b-needs-human-review` are
   * orchestrator-managed lanes (ADR-0026) — listed but unusual to pick by
   * hand, hence the discreet labels.
   */
  readonly laneOptions: ReadonlyArray<{ state: string; label: string }> = [
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

  toggleMenu(event: MouseEvent) {
    event.stopPropagation();
    this.menuOpen.update(v => !v);
  }

  closeMenu() {
    this.menuOpen.set(false);
  }

  onDeleteClick(event: MouseEvent) {
    event.stopPropagation();
    this.menuOpen.set(false);
    this.deleteRequested.emit();
  }

  @HostListener('document:click')
  onDocumentClick() {
    if (this.menuOpen()) this.menuOpen.set(false);
  }

  @HostListener('document:keydown.escape')
  onEscape() {
    if (this.menuOpen()) this.menuOpen.set(false);
  }

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
