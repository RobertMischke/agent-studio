import { ChangeDetectionStrategy, Component, ElementRef, ViewChild, effect, input, output } from '@angular/core';
import { JobInfo } from '../../../models/job.model';
import { stateLabel as fmtStateLabel } from '../../../services/format.util';

/**
 * Top header of the job-detail view: back button, editable title,
 * state pill, and the "Complete & Next" review action. Title-edit
 * state is owned by the parent and passed via inputs/outputs.
 */
@Component({
  selector: 'app-detail-header',
  standalone: true,
  changeDetection: ChangeDetectionStrategy.OnPush,
  templateUrl: './detail-header.component.html'
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

  stateLabel(state: string): string { return fmtStateLabel(state); }
}
