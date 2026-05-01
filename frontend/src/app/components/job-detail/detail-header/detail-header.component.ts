import { ChangeDetectionStrategy, Component, ElementRef, ViewChild, computed, effect, inject, input, output } from '@angular/core';
import { JobInfo } from '../../../models/job.model';
import {
  formatDateTime as fmtDateTime,
  formatRelativeShort as fmtRelativeShort,
  stateLabel as fmtStateLabel
} from '../../../services/format.util';
import { NowTickService } from '../../../services/now-tick.service';
import { projectIdentity } from '../../../services/project-identity.util';

/**
 * Top header of the job-detail view: back button, editable title,
 * state pill, and the "Complete & Next" review action. Title-edit
 * state is owned by the parent and passed via inputs/outputs.
 */
@Component({
  selector: 'app-detail-header',
  standalone: true,
  changeDetection: ChangeDetectionStrategy.OnPush,
  templateUrl: './detail-header.component.html',
  styles: [`
    /* Project identity chip above the title — same shape as the board cards
       so the user keeps one visual cue across views. Hosted here (rather
       than in job-detail.ts) to keep the detail-header self-contained. */
    :host .detail__project {
      display: inline-flex;
      align-items: center;
      gap: 6px;
      padding: 2px 10px 2px 3px;
      margin: 0 0 6px 0;
      border-radius: 999px;
      font-size: 11px;
      text-transform: uppercase;
      letter-spacing: 0.05em;
      font-weight: 600;
      color: var(--project-color, #8b5cf6);
      background: var(--project-soft, rgba(139,92,246,0.10));
      border: 1px solid var(--project-border, transparent);
      max-width: fit-content;
    }
    :host .detail__project-disk {
      display: inline-grid;
      place-items: center;
      width: 18px;
      height: 18px;
      border-radius: 999px;
      background: var(--project-color, #8b5cf6);
      color: var(--project-on, #0b1020);
      font-size: 11px;
      font-weight: 800;
    }
  `]
})
export class DetailHeaderComponent {
  readonly info = input.required<JobInfo>();
  readonly editingTitle = input(false);
  readonly titleDraft = input<string>('');
  readonly savingTitle = input(false);
  readonly isReview = input(false);
  readonly completingAndNext = input(false);

  readonly back = output<void>();
  readonly startTitleEdit = output<void>();
  readonly cancelTitleEdit = output<void>();
  readonly saveTitle = output<void>();
  readonly titleDraftChange = output<string>();
  readonly completeAndNext = output<void>();

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
